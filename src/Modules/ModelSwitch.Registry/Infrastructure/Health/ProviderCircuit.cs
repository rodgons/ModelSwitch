using System.Diagnostics;
using System.Runtime.ExceptionServices;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Registry.Contracts;
using Polly;
using Polly.CircuitBreaker;
using CircuitState = ModelSwitch.Registry.Contracts.CircuitState;
using PollyState = Polly.CircuitBreaker.CircuitState;

namespace ModelSwitch.Registry.Infrastructure.Health;

/// <summary>
/// The circuit breaker of one Provider Model on this instance. Polly's circuit-breaker state machine decides when to open,
/// admits the half-open probe and rejects the rest (ADR-0004, ADR-0005). Polly does not expose its sampling window, so a small
/// window of outcomes is kept beside it for the health snapshot. Consecutive re-opens double the break up to the maximum.
/// </summary>
internal sealed class ProviderCircuit
{
    private readonly Guid _providerModelId;
    private readonly CircuitOptions _options;
    private readonly TimeProvider _time;
    private readonly Action<Guid, CircuitState> _onTransition;
    private readonly CircuitBreakerStateProvider _stateProvider = new();
    private readonly ResiliencePipeline<Counted> _pipeline;
    private readonly Lock _gate = new();
    private readonly Queue<(DateTimeOffset At, bool Failed)> _window = new();
    private int _consecutiveOpens;
    private DateTimeOffset _openUntil;

    public ProviderCircuit(Guid providerModelId, CircuitOptions options, TimeProvider time, Action<Guid, CircuitState> onTransition)
    {
        _providerModelId = providerModelId;
        _options = options;
        _time = time;
        _onTransition = onTransition;
        _pipeline = new ResiliencePipelineBuilder<Counted> { TimeProvider = time }
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<Counted>
            {
                FailureRatio = options.FailureRatio,
                MinimumThroughput = options.MinimumCalls,
                SamplingDuration = options.SamplingWindow,
                BreakDuration = options.BreakDuration,
                BreakDurationGenerator = _ => ValueTask.FromResult(NextBreak()),
                ShouldHandle = args => ValueTask.FromResult(args.Outcome.Result?.CountsAsFailure == true),
                StateProvider = _stateProvider,
                OnOpened = args =>
                {
                    lock (_gate)
                    {
                        _consecutiveOpens++;
                        _openUntil = _time.GetUtcNow() + args.BreakDuration;
                    }

                    _onTransition(_providerModelId, CircuitState.Open);
                    return default;
                },
                OnHalfOpened = _ =>
                {
                    _onTransition(_providerModelId, CircuitState.HalfOpen);
                    return default;
                },
                OnClosed = _ =>
                {
                    lock (_gate)
                    {
                        _consecutiveOpens = 0;
                    }

                    _onTransition(_providerModelId, CircuitState.Closed);
                    return default;
                },
            })
            .Build();
    }

    public CircuitState State
    {
        get
        {
            switch (_stateProvider.CircuitState)
            {
                case PollyState.Open:
                    // Polly leaves the state at Open until the next call after the break; that call is the half-open probe.
                    return RemainingBreak() is null ? CircuitState.HalfOpen : CircuitState.Open;
                case PollyState.Isolated:
                    return CircuitState.Open;
                case PollyState.HalfOpen:
                    return CircuitState.HalfOpen;
                default:
                    return CircuitState.Closed;
            }
        }
    }

    /// <summary>Time left in the current break, or null when the circuit is not open or the break has elapsed.</summary>
    public TimeSpan? RemainingBreak()
    {
        if (_stateProvider.CircuitState is not (PollyState.Open or PollyState.Isolated))
        {
            return null;
        }

        DateTimeOffset until;
        lock (_gate)
        {
            until = _openUntil;
        }

        var remaining = until - _time.GetUtcNow();
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    public (double FailureRate, int Calls) Window()
    {
        lock (_gate)
        {
            Prune();
            if (_window.Count == 0)
            {
                return (0, 0);
            }

            var failed = _window.Count(w => w.Failed);
            return ((double)failed / _window.Count, _window.Count);
        }
    }

    public async Task<GuardedCall<T>> ExecuteAsync<T>(Func<CancellationToken, Task<T>> attempt, Func<T, ProviderFailure?> failureOf, CancellationToken cancellationToken)
    {
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        try
        {
            var outcome = await _pipeline.ExecuteOutcomeAsync(
                async (ctx, state) =>
                {
                    try
                    {
                        var value = await state.Attempt(ctx.CancellationToken);
                        var failure = state.FailureOf(value);
                        var counts = failure is not null && CircuitFailureRules.Counts(failure);
                        Record(failed: counts);
                        return Outcome.FromResult(new Counted(value, counts));
                    }
#pragma warning disable CA1031 // The exception is handed back through the outcome and rethrown unchanged by the caller below.
                    catch (Exception exception)
#pragma warning restore CA1031
                    {
                        return Outcome.FromException<Counted>(exception);
                    }
                },
                context,
                (Attempt: attempt, FailureOf: failureOf));

            switch (outcome.Exception)
            {
                case null:
                    return new GuardedCall<T>(Rejected: false, RetryAfter: null, (T?)outcome.Result!.Value);
                case BrokenCircuitException broken:
                    return new GuardedCall<T>(Rejected: true, broken.RetryAfter ?? RemainingBreak(), default);
                default:
                    ExceptionDispatchInfo.Capture(outcome.Exception).Throw();
                    throw outcome.Exception; // unreachable; satisfies definite assignment
            }
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    private TimeSpan NextBreak()
    {
        int opens;
        lock (_gate)
        {
            opens = _consecutiveOpens;
        }

        var seconds = _options.BreakDuration.TotalSeconds * Math.Pow(2, Math.Min(opens, 20));
        return TimeSpan.FromSeconds(Math.Min(seconds, _options.MaxBreakDuration.TotalSeconds));
    }

    private void Record(bool failed)
    {
        lock (_gate)
        {
            _window.Enqueue((_time.GetUtcNow(), failed));
            Prune();
        }
    }

    private void Prune()
    {
        var cutoff = _time.GetUtcNow() - _options.SamplingWindow;
        while (_window.Count > 0 && _window.Peek().At < cutoff)
        {
            _window.Dequeue();
        }
    }

    private sealed record Counted(object? Value, bool CountsAsFailure);
}

internal static class CircuitLog
{
    public static void AddSpanEvent(Guid providerModelId, CircuitState to)
        => Activity.Current?.AddEvent(new ActivityEvent(
            "circuit.transition",
            tags: new ActivityTagsCollection { { "provider_model_id", providerModelId }, { "to_state", to.ToString() } }));
}
