using ModelSwitch.Adapters.Abstractions;

namespace ModelSwitch.Registry.Contracts;

/// <summary>The state of one circuit breaker (observability.md: 0 closed, 1 half-open, 2 open).</summary>
public enum CircuitState
{
    Closed = 0,
    HalfOpen = 1,
    Open = 2,
}

/// <summary>What this instance currently believes about one Provider Model (Provider Health, CONTEXT.md).</summary>
/// <param name="ProviderModelId">The Provider Model.</param>
/// <param name="State">The circuit state.</param>
/// <param name="RemainingBreak">Time until the circuit lets a probe through; set only while the state is Open.</param>
/// <param name="WindowFailureRate">Failures over calls in the sampling window; 0 when there were no calls.</param>
/// <param name="WindowCalls">Calls in the sampling window.</param>
public sealed record ProviderModelHealth(Guid ProviderModelId, CircuitState State, TimeSpan? RemainingBreak, double WindowFailureRate, int WindowCalls);

/// <summary>
/// The result of a call guarded by a circuit breaker. When <see cref="Rejected"/> the call was not made because the circuit is
/// open (or a half-open probe is already in flight).
/// </summary>
/// <param name="Rejected">True when the breaker refused the call.</param>
/// <param name="RetryAfter">When rejected, the remaining break if known.</param>
/// <param name="Value">The call's result; default when rejected.</param>
public sealed record GuardedCall<T>(bool Rejected, TimeSpan? RetryAfter, T? Value);

/// <summary>
/// Per-instance Provider Health: one circuit breaker per Provider Model (ADR-0005), kept in memory. The breaker gates the
/// call itself, so a half-open circuit admits exactly one probe at a time.
/// </summary>
public interface IProviderHealth
{
    /// <summary>The Provider Models whose circuit is open right now, with the remaining break. This is <c>RoutingInput.OpenCircuits</c>.</summary>
    IReadOnlyDictionary<Guid, TimeSpan> OpenCircuits();

    /// <summary>The circuit state of a Provider Model; Closed when this instance has never called it.</summary>
    CircuitState StateOf(Guid providerModelId);

    /// <summary>Health of every Provider Model this instance has made a call to.</summary>
    IReadOnlyList<ProviderModelHealth> Snapshot();

    /// <summary>
    /// Runs <paramref name="attempt"/> through the Provider Model's breaker. <paramref name="failureOf"/> extracts the Provider
    /// failure of the result, if any, and the breaker decides whether it counts (transient, ambiguous, 401/403 and billing
    /// failures do; request errors and cancellations do not). An exception from <paramref name="attempt"/>, including
    /// cancellation, propagates and is not counted.
    /// </summary>
    Task<GuardedCall<T>> ExecuteAsync<T>(Guid providerModelId, Func<CancellationToken, Task<T>> attempt, Func<T, ProviderFailure?> failureOf, CancellationToken cancellationToken);
}
