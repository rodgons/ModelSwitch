using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Inference.Application;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Registry.Infrastructure.Health;
using ModelSwitch.Routing.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.Usage.Contracts;
using ModelSwitch.Usage.Domain;
using Shouldly;

namespace ModelSwitch.UnitTests.Inference;

/// <summary>
/// The first-token rule (routing-and-resilience.md section 6): until the first content chunk has come from a Provider a stream is
/// retried and falls back like any other call; from then on it is never replayed, and a failure is <c>stream_interrupted</c>.
/// </summary>
public sealed class AttemptExecutorStreamingTests : IDisposable
{
    private static readonly DateTimeOffset _start = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(_start);
    private readonly Fleet _fleet = new();
    private readonly ProviderHealth _health;
    private readonly AttemptExecutor _executor;
    private readonly List<AttemptRecord> _attempts = [];

    public AttemptExecutorStreamingTests()
    {
        _health = new ProviderHealth(new CircuitOptions(), _time, NullLogger<ProviderHealth>.Instance);
        _executor = new AttemptExecutor(_fleet, _health, new InputTokenEstimator(), new FixedJitter(), _time, NullLogger<AttemptExecutor>.Instance);
    }

    public void Dispose() => _health.Dispose();

    private static NeutralChatRequest Chat() => new([new NeutralMessage(MessageRole.User, "tell me a story about a dragon")]);

    private static StreamEvent.ContentDelta Text(string text) => new(text);

    private static StreamEvent.Finish Stop() => new(FinishReason.Stop);

    private static StreamEvent.Usage Used(int input = 3, int output = 4) => new(new NeutralUsage(input, output, null));

    private static StreamEvent.Failed Boom(FailureClass failureClass = FailureClass.Transient, string code = ProviderFailureCodes.ServerError, int? status = 503)
        => new(new ProviderFailure(failureClass, code, status, null, RequestSent: true));

    // OpenAsync drives the fake clock in a tight loop, so on a cold run it can outpace the executor by minutes: the Deadline must be out of reach.
    private const int _dayMs = 86_400_000;

    private ExecutionPlan Plan(params string[] models) => Plan(models, attemptTimeoutMs: 30_000);

    private ExecutionPlan Plan(string[] models, int attemptTimeoutMs, int maxRetries = 1)
        => new(
            [.. models.Select(_fleet.Candidate)],
            new ExecutionSettings(_dayMs, attemptTimeoutMs, 4, maxRetries, new BackoffSettings(200, 2_000, FullJitter: true)),
            _time.GetUtcNow().AddMilliseconds(_dayMs));

    // Backoff waits and stall timers run on the fake clock, so it has to be driven until the executor finishes.
    private async Task<Result<StreamingAttempt>> OpenAsync(ExecutionPlan plan, CancellationTokenSource? abort = null)
    {
        var pending = _executor.ExecuteStreamAsync(plan, Chat(), _attempts, abort?.Token ?? TestContext.Current.CancellationToken);
        while (!pending.IsCompleted)
        {
            _time.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Yield();
        }

        return await pending;
    }

    private static async Task<List<StreamPart>> DrainAsync(StreamingAttempt attempt, CancellationToken cancellationToken)
    {
        var parts = new List<StreamPart>();
        await foreach (var part in attempt.ReadAsync(cancellationToken))
        {
            parts.Add(part);
        }

        return parts;
    }

    [Fact]
    public async Task A_stream_that_completes_forwards_its_content_and_records_one_succeeded_attempt_with_reported_usage()
    {
        _fleet.Script("a", Text("Hel"), Text("lo"), Stop(), Used(10, 2));

        var started = await OpenAsync(Plan("a"));
        var parts = await DrainAsync(started.Value, TestContext.Current.CancellationToken);

        parts.Select(p => p is StreamPart.Content c ? c.Text : p.GetType().Name).ShouldBe(["Hel", "lo", "Completed"]);
        var completed = parts.OfType<StreamPart.Completed>().Single();
        (completed.Reason, completed.UsageSource, completed.Attempts).ShouldBe((FinishReason.Stop, "reported", 1));
        completed.Usage.ShouldBe(new NeutralUsage(10, 2, null));
        var attempt = _attempts.Single();
        (attempt.Kind, attempt.Status, attempt.UsageSource, attempt.InputTokens, attempt.OutputTokens).ShouldBe((AttemptKind.Primary, AttemptStatus.Succeeded, UsageSource.Reported, 10, 2));
        attempt.TtftMs.ShouldNotBeNull();
        _fleet.DisposedStreams.ShouldBe(1);
    }

    [Fact]
    public async Task A_failure_before_the_first_token_is_retried_then_falls_back_and_the_client_has_seen_nothing()
    {
        _fleet.Script("a", Boom());
        _fleet.Script("b", Text("ok"), Stop(), Used());

        var started = await OpenAsync(Plan("a", "b"));
        var parts = await DrainAsync(started.Value, TestContext.Current.CancellationToken);

        parts.OfType<StreamPart.Completed>().Single().Attempts.ShouldBe(3);
        _fleet.Calls("a").ShouldBe(2);
        _fleet.Calls("b").ShouldBe(1);
        _attempts.Select(a => (a.Kind, a.Status)).ShouldBe(
        [
            (AttemptKind.Primary, AttemptStatus.Failed),
            (AttemptKind.Retry, AttemptStatus.Failed),
            (AttemptKind.Fallback, AttemptStatus.Succeeded),
        ]);
    }

    [Fact]
    public async Task Role_only_and_empty_deltas_are_not_the_first_token_so_a_later_failure_still_falls_back()
    {
        _fleet.Script("a", Text(string.Empty), Text(string.Empty), Boom());
        _fleet.Script("b", Text("ok"), Stop(), Used());

        var started = await OpenAsync(Plan(["a", "b"], attemptTimeoutMs: 30_000, maxRetries: 0));
        var parts = await DrainAsync(started.Value, TestContext.Current.CancellationToken);

        parts.OfType<StreamPart.Content>().Single().Text.ShouldBe("ok");
        _fleet.Calls("b").ShouldBe(1);
        _attempts.Select(a => a.Status).ShouldBe([AttemptStatus.Failed, AttemptStatus.Succeeded]);
    }

    [Fact]
    public async Task Usage_that_arrives_before_the_first_token_is_kept()
    {
        _fleet.Script("a", Used(7, 5), Text("hi"), Stop());

        var started = await OpenAsync(Plan("a"));
        var parts = await DrainAsync(started.Value, TestContext.Current.CancellationToken);

        parts.OfType<StreamPart.Completed>().Single().Usage.ShouldBe(new NeutralUsage(7, 5, null));
    }

    [Fact]
    public async Task An_empty_completion_is_a_started_stream_that_completes_without_content()
    {
        _fleet.Script("a", Stop(), Used(3, 0));

        var started = await OpenAsync(Plan("a"));
        var parts = await DrainAsync(started.Value, TestContext.Current.CancellationToken);

        parts.Single().ShouldBeOfType<StreamPart.Completed>();
        _attempts.Single().Status.ShouldBe(AttemptStatus.Succeeded);
    }

    [Fact]
    public async Task A_failure_after_the_first_token_is_stream_interrupted_with_no_retry_and_no_fallback()
    {
        _fleet.Script("a", Text("one"), Text("two"), Boom(FailureClass.Ambiguous, ProviderFailureCodes.ConnectionReset, null));
        _fleet.Script("b", Text("never"), Stop(), Used());

        var started = await OpenAsync(Plan("a", "b"));
        var parts = await DrainAsync(started.Value, TestContext.Current.CancellationToken);

        parts.Count.ShouldBe(3);
        parts[0].ShouldBeOfType<StreamPart.Content>();
        parts[1].ShouldBeOfType<StreamPart.Content>();
        parts[2].ShouldBeOfType<StreamPart.Interrupted>().Error.Code.ShouldBe(ErrorCode.StreamInterrupted);
        _fleet.Calls("a").ShouldBe(1, "no replay");
        _fleet.Calls("b").ShouldBe(0, "no Fallback after the First Token");
        var attempt = _attempts.Single();
        (attempt.Status, attempt.ErrorClass, attempt.ErrorCode, attempt.ProviderErrorType).ShouldBe((AttemptStatus.Ambiguous, AttemptErrorClass.Ambiguous, "stream_interrupted", "connection_reset"));
        attempt.UsageSource.ShouldBe(UsageSource.Estimated);
        attempt.InputTokens!.Value.ShouldBeGreaterThan(0);
        attempt.OutputTokens!.Value.ShouldBeGreaterThan(0, "counted from the forwarded text");
    }

    [Fact]
    public async Task A_transient_failure_after_the_first_token_is_a_failed_attempt_and_still_not_retried()
    {
        _fleet.Script("a", Text("one"), Boom());

        var started = await OpenAsync(Plan("a"));
        var parts = await DrainAsync(started.Value, TestContext.Current.CancellationToken);

        parts.Last().ShouldBeOfType<StreamPart.Interrupted>();
        _fleet.Calls("a").ShouldBe(1);
        _attempts.Single().Status.ShouldBe(AttemptStatus.Failed);
        _attempts.Single().ErrorClass.ShouldBe(AttemptErrorClass.Transient);
    }

    [Fact]
    public async Task A_stream_without_reported_usage_is_estimated_from_the_request_and_the_forwarded_text()
    {
        _fleet.Script("a", Text("a short answer"), Stop());

        var started = await OpenAsync(Plan("a"));
        var parts = await DrainAsync(started.Value, TestContext.Current.CancellationToken);

        var completed = parts.OfType<StreamPart.Completed>().Single();
        completed.UsageSource.ShouldBe("estimated");
        completed.Usage.InputTokens.ShouldBeGreaterThan(0);
        completed.Usage.OutputTokens.ShouldBeGreaterThan(0);
        _attempts.Single().UsageSource.ShouldBe(UsageSource.Estimated);
    }

    [Fact]
    public async Task A_provider_that_stays_silent_past_the_attempt_timeout_before_the_first_token_falls_back()
    {
        _fleet.Script("a", Hang());
        _fleet.Script("b", Text("ok"), Stop(), Used());

        var started = await OpenAsync(Plan(["a", "b"], attemptTimeoutMs: 2_000, maxRetries: 0));
        var parts = await DrainAsync(started.Value, TestContext.Current.CancellationToken);

        parts.OfType<StreamPart.Completed>().ShouldHaveSingleItem();
        _attempts.Select(a => (a.Kind, a.Status, a.ProviderErrorType)).ShouldBe(
        [
            (AttemptKind.Primary, AttemptStatus.Ambiguous, "timeout"),
            (AttemptKind.Fallback, AttemptStatus.Succeeded, null),
        ]);
    }

    [Fact]
    public async Task A_stream_that_stalls_after_the_first_token_is_interrupted_not_hung()
    {
        _fleet.Script("a", Text("one"), Hang());

        var started = await OpenAsync(Plan(["a"], attemptTimeoutMs: 2_000));
        var reading = DrainAsync(started.Value, TestContext.Current.CancellationToken);
        while (!reading.IsCompleted)
        {
            _time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Yield();
        }

        var parts = await reading;
        parts.Last().ShouldBeOfType<StreamPart.Interrupted>();
        _attempts.Single().ProviderErrorType.ShouldBe("timeout");
    }

    [Fact]
    public async Task A_client_abort_mid_stream_aborts_the_provider_stream_and_records_the_attempt_ambiguous_with_estimated_usage()
    {
        _fleet.Script("a", Text("one"), Hang());
        using var abort = new CancellationTokenSource();
        var started = await OpenAsync(Plan("a"), abort);

        var reading = DrainAsync(started.Value, abort.Token);
        await abort.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => reading);
        var attempt = _attempts.Single();
        (attempt.Status, attempt.ProviderErrorType).ShouldBe((AttemptStatus.Ambiguous, "client_cancelled"));
        attempt.UsageSource.ShouldBe(UsageSource.Estimated);
        _fleet.DisposedStreams.ShouldBe(1, "the Provider connection is closed");
        _health.StateOf(_fleet.IdOf("a")).ShouldBe(CircuitState.Closed, "a client abort says nothing about the Provider");
    }

    [Fact]
    public async Task A_consumer_that_stops_early_closes_the_provider_stream_and_records_the_attempt()
    {
        _fleet.Script("a", Text("one"), Text("two"), Stop(), Used());
        var started = await OpenAsync(Plan("a"));

        await foreach (var part in started.Value.ReadAsync(TestContext.Current.CancellationToken))
        {
            part.ShouldBeOfType<StreamPart.Content>();
            break;
        }

        _attempts.Single().Status.ShouldBe(AttemptStatus.Ambiguous);
        _fleet.DisposedStreams.ShouldBe(1);
    }

    [Fact]
    public async Task A_cancelled_wait_for_the_first_token_records_ambiguous_and_rethrows()
    {
        _fleet.Script("a", Hang());
        using var abort = new CancellationTokenSource();

        var pending = _executor.ExecuteStreamAsync(Plan("a"), Chat(), _attempts, abort.Token);
        await Task.Delay(30, TestContext.Current.CancellationToken);
        await abort.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        _attempts.Single().ProviderErrorType.ShouldBe("client_cancelled");
        _fleet.DisposedStreams.ShouldBe(1);
    }

    [Fact]
    public async Task Failures_after_the_first_token_feed_the_circuit_breaker()
    {
        _fleet.Script("a", Text("x"), Boom());
        // Each stream is one successful call (the open) and one failure (the cut): at 50 % over ten calls the circuit opens.
        for (var i = 0; i < 10 && _health.StateOf(_fleet.IdOf("a")) != CircuitState.Open; i++)
        {
            _attempts.Clear();
            var started = await OpenAsync(Plan("a"));
            await DrainAsync(started.Value, TestContext.Current.CancellationToken);
        }

        _health.StateOf(_fleet.IdOf("a")).ShouldBe(CircuitState.Open);
    }

    [Fact]
    public async Task A_candidate_with_an_open_circuit_is_skipped_for_a_stream_too()
    {
        _fleet.Script("a", Text("x"), Stop(), Used());
        _fleet.Script("b", Text("y"), Stop(), Used());
        for (var i = 0; i < 10; i++)
        {
            await _health.ExecuteAsync(
                _fleet.IdOf("a"),
                _ => Task.FromResult(new CompletionOutcome.Failure(new ProviderFailure(FailureClass.Transient, ProviderFailureCodes.ServerError, 503, null, true))),
                static outcome => outcome.Error,
                CancellationToken.None);
        }

        var started = await OpenAsync(Plan("a", "b"));
        await DrainAsync(started.Value, TestContext.Current.CancellationToken);

        _fleet.Calls("a").ShouldBe(0);
        _attempts.Select(a => a.Status).ShouldBe([AttemptStatus.SkippedCircuitOpen, AttemptStatus.Succeeded]);
    }

    [Fact]
    public async Task A_permanent_request_error_before_the_first_token_stops_without_fallback()
    {
        _fleet.Script("a", Boom(FailureClass.Permanent, ProviderFailureCodes.BadRequest, 400));
        _fleet.Script("b", Text("y"), Stop(), Used());

        var started = await OpenAsync(Plan("a", "b"));

        started.Error.Code.ShouldBe(ErrorCode.ProviderError);
        _fleet.Calls("b").ShouldBe(0);
    }

    // ---- fakes ------------------------------------------------------------------------------------------------------

    private sealed class FixedJitter : IJitterSource
    {
        public double Next() => 0.5;
    }

    /// <summary>A marker in a script: the Adapter goes quiet here until its token is cancelled.</summary>
    private static object Hang() => _hangMarker;

    private static readonly object _hangMarker = new();

    private sealed class Fleet : IAdapterFactory
    {
        private readonly Dictionary<string, Guid> _ids = [];
        private readonly Dictionary<string, List<object>> _scripts = [];
        private readonly Dictionary<string, int> _calls = [];

        public int DisposedStreams { get; set; }

        public Guid IdOf(string model) => _ids.TryGetValue(model, out var id) ? id : _ids[model] = Guid.CreateVersion7();

        public int Calls(string model) => _calls.GetValueOrDefault(model);

        public void Script(string model, params object[] events)
        {
            _scripts[model] = [.. events];
            _ = IdOf(model);
        }

        public RankedCandidate Candidate(string model) => new(
            IdOf(model),
            IdOf(model),
            "p",
            model,
            0m,
            new ProviderModelTarget(IdOf(model), IdOf(model), "p", "fake", new Uri("http://x/"), null, TimeSpan.FromSeconds(30), model, "o200k_base"));

        public ValueTask<Result<AdapterBinding>> BindAsync(ProviderModelTarget target, CancellationToken cancellationToken)
            => ValueTask.FromResult<Result<AdapterBinding>>(
                new AdapterBinding(new ScriptedAdapter(this, target.ProviderModelIdentifier), new AdapterContext(target.Endpoint, null, target.ProviderModelIdentifier, target.RequestTimeout)));

        private sealed class ScriptedAdapter(Fleet fleet, string model) : IProviderAdapter
        {
            public string AdapterType => "fake";

            public Task<CompletionOutcome> CompleteAsync(AdapterContext ctx, NeutralChatRequest request, CancellationToken ct) => throw new NotSupportedException();

            public async IAsyncEnumerable<StreamEvent> StreamAsync(AdapterContext ctx, NeutralChatRequest request, [EnumeratorCancellation] CancellationToken ct)
            {
                fleet._calls[model] = fleet._calls.GetValueOrDefault(model) + 1;
                try
                {
                    foreach (var scripted in fleet._scripts[model])
                    {
                        if (ReferenceEquals(scripted, _hangMarker))
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                        }

                        yield return (StreamEvent)scripted;
                    }
                }
                finally
                {
                    fleet.DisposedStreams++;
                }
            }

            public Task<ConnectivityResult> CheckAsync(AdapterContext ctx, CancellationToken ct) => throw new NotSupportedException();

            public Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(AdapterContext ctx, CancellationToken ct) => throw new NotSupportedException();
        }
    }
}
