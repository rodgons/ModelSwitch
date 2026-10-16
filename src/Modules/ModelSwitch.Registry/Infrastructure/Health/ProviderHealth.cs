using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Registry.Contracts;

namespace ModelSwitch.Registry.Infrastructure.Health;

/// <summary>The in-memory <see cref="IProviderHealth"/>: one <see cref="ProviderCircuit"/> per Provider Model, created on first use.</summary>
internal sealed partial class ProviderHealth : IProviderHealth, IDisposable
{
    private readonly ConcurrentDictionary<Guid, ProviderCircuit> _circuits = new();
    private readonly CircuitOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ProviderHealth> _logger;
    private readonly Meter _meter = new("ModelSwitch.Circuit");
    private readonly Counter<long> _transitions;

    public ProviderHealth(CircuitOptions options, TimeProvider time, ILogger<ProviderHealth> logger)
    {
        _options = options;
        _time = time;
        _logger = logger;
        _transitions = _meter.CreateCounter<long>("modelswitch.circuit.transitions");
        _meter.CreateObservableGauge(
            "modelswitch.circuit.state",
            () => _circuits.Select(c => new Measurement<int>((int)c.Value.State, new KeyValuePair<string, object?>("model", c.Key.ToString()))).ToArray());
    }

    public IReadOnlyDictionary<Guid, TimeSpan> OpenCircuits()
    {
        var open = new Dictionary<Guid, TimeSpan>();
        foreach (var (id, circuit) in _circuits)
        {
            if (circuit.State == CircuitState.Open && circuit.RemainingBreak() is { } remaining)
            {
                open[id] = remaining;
            }
        }

        return open;
    }

    public CircuitState StateOf(Guid providerModelId)
        => _circuits.TryGetValue(providerModelId, out var circuit) ? circuit.State : CircuitState.Closed;

    public IReadOnlyList<ProviderModelHealth> Snapshot()
        => [.. _circuits.Select(c =>
        {
            var (rate, calls) = c.Value.Window();
            return new ProviderModelHealth(c.Key, c.Value.State, c.Value.RemainingBreak(), rate, calls);
        })];

    public Task<GuardedCall<T>> ExecuteAsync<T>(Guid providerModelId, Func<CancellationToken, Task<T>> attempt, Func<T, ProviderFailure?> failureOf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(failureOf);
        var circuit = _circuits.GetOrAdd(providerModelId, id => new ProviderCircuit(id, _options, _time, OnTransition));
        return circuit.ExecuteAsync(attempt, failureOf, cancellationToken);
    }

    public void Dispose() => _meter.Dispose();

    private void OnTransition(Guid providerModelId, CircuitState state)
    {
        LogTransition(_logger, providerModelId, state);
        CircuitLog.AddSpanEvent(providerModelId, state);
        _transitions.Add(1, new KeyValuePair<string, object?>("model", providerModelId.ToString()), new KeyValuePair<string, object?>("state", state.ToString().ToLowerInvariant()));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Circuit for Provider Model {ProviderModelId} is now {State}.")]
    private static partial void LogTransition(ILogger logger, Guid providerModelId, CircuitState state);
}
