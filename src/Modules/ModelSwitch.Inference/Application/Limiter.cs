using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using ModelSwitch.Configuration.Contracts;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.Routing.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Inference.Application;

/// <summary>The three answers of the atomic acquire script.</summary>
internal enum LimitOutcome
{
    Allowed,
    RateLimited,
    ConcurrencyLimited,
}

/// <summary>What the store answered. <see cref="RetryAfter"/> is set when refused.</summary>
internal sealed record LimitDecision(LimitOutcome Outcome, TimeSpan RetryAfter, RateLimitState? Rate);

/// <summary>
/// The shared counters behind the limits (Valkey in production). Both methods throw on any fault; <see cref="Limiter"/> decides
/// what a fault means. <see cref="TryAcquireAsync"/> is atomic: it either takes the Rate Limit token and the lease or neither.
/// </summary>
internal interface ILimitStore
{
    Task<LimitDecision> TryAcquireAsync(Guid tenantId, Guid leaseId, int? requestsPerMinute, int? maxConcurrent, TimeSpan leaseTtl, CancellationToken cancellationToken);

    Task ReleaseAsync(Guid tenantId, Guid leaseId, CancellationToken cancellationToken);
}

/// <summary>The instruments of the limits step (observability.md section 3).</summary>
internal sealed class LimitsMetrics
{
    public const string MeterName = "ModelSwitch";

    private readonly Counter<long> _rejections;
    private readonly Counter<long> _degraded;

    public LimitsMetrics(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        var meter = meters.Create(MeterName);
        _rejections = meter.CreateCounter<long>("modelswitch.limits.rejections", "{request}", "Requests refused by a limit.");
        _degraded = meter.CreateCounter<long>("modelswitch.limits.degraded", "{request}", "Requests let through because Valkey was unavailable (fail-open).");
    }

    public void Rejected(string kind) => _rejections.Add(1, new KeyValuePair<string, object?>("kind", kind));

    public void Degraded() => _degraded.Add(1);
}

/// <summary>
/// <see cref="ILimiter"/>: reads the Tenant's limits from the configuration snapshot (a request keeps the one it started
/// with) and asks the <see cref="ILimitStore"/> once. A Tenant with neither limit costs no Valkey call. A store fault or a call
/// that outlasts the budget is resolved by the failure mode: <c>Closed</c> refuses with <c>limits_unavailable</c>, <c>Open</c>
/// admits the request and counts it as degraded.
/// </summary>
internal sealed partial class Limiter(
    IConfigurationSnapshotProvider configuration,
    ILimitStore store,
    LimitsOptions options,
    LimitsMetrics metrics,
    TimeProvider time,
    ILogger<Limiter> logger) : ILimiter
{
    /// <summary>A refused request is told to come back after about a second when the store cannot say better (lease expiry is far away).</summary>
    internal static readonly TimeSpan ConcurrencyRetryAfter = TimeSpan.FromSeconds(1);

    public async Task<Result<LimitLease>> AcquireAsync(LimitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = configuration.Current;
        if (!snapshot.TenantLimits.TryGetValue(request.TenantId, out var limits) || (limits.RequestsPerMinute is null && limits.MaxConcurrent is null))
        {
            return LimitLease.Unlimited();
        }

        var ttl = LeaseTtl(snapshot, request);
        LimitDecision decision;
        try
        {
            decision = await store
                .TryAcquireAsync(request.TenantId, request.LeaseId, limits.RequestsPerMinute, limits.MaxConcurrent, ttl, cancellationToken)
                .WaitAsync(options.ValkeyTimeout, time, cancellationToken);
        }
#pragma warning disable CA1031 // Any fault of the store, including a timeout, is the failure mode's business.
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return Unavailable(exception);
        }

        switch (decision.Outcome)
        {
            case LimitOutcome.Allowed:
                return new LimitLease(decision.Rate, () => ReleaseAsync(request, limits.MaxConcurrent is not null));
            case LimitOutcome.RateLimited:
                metrics.Rejected("rate");
                return new Error(ErrorCode.RateLimitExceeded, "Rate limit exceeded. Retry after the time given in Retry-After.") { RetryAfter = decision.RetryAfter };
            default:
                metrics.Rejected("concurrency");
                return new Error(ErrorCode.ConcurrencyLimitExceeded, "Concurrency limit exceeded. Retry after the time given in Retry-After.") { RetryAfter = decision.RetryAfter };
        }
    }

    /// <summary>
    /// The Deadline of the alias's Routing Policy (the client may shorten it) plus a grace. An unknown alias falls back to the
    /// default Deadline; routing rejects it right after, and the lease is released then.
    /// </summary>
    internal static TimeSpan LeaseTtl(ConfigurationSnapshot snapshot, LimitRequest request)
    {
        var settings = snapshot.Routing.AliasToPolicy.TryGetValue(request.Alias, out var policyId) && snapshot.Routing.Policies.TryGetValue(policyId, out var policy)
            ? policy.Definition.Execution
            : ExecutionSettings.Default;
        var deadline = TimeSpan.FromMilliseconds(settings.DeadlineMs);
        if (request.Timeout is { } requested && requested < deadline)
        {
            deadline = requested;
        }

        return deadline + LimitsOptions.LeaseGrace;
    }

    private Result<LimitLease> Unavailable(Exception exception)
    {
        LogUnavailable(logger, exception, options.FailureMode);
        if (options.FailureMode == LimitsFailureMode.Open)
        {
            metrics.Degraded();
            return LimitLease.Unlimited();
        }

        return new Error(ErrorCode.LimitsUnavailable, "Rate and concurrency limits cannot be checked right now. Retry shortly.") { RetryAfter = ConcurrencyRetryAfter };
    }

    // Releasing must not fail the response or wait on the client: it gets the same budget and, if it fails, the lease expires.
    private async ValueTask ReleaseAsync(LimitRequest request, bool holdsLease)
    {
        if (!holdsLease)
        {
            return;
        }

        try
        {
            await store.ReleaseAsync(request.TenantId, request.LeaseId, CancellationToken.None).WaitAsync(options.ValkeyTimeout, time);
        }
#pragma warning disable CA1031 // A failed release only delays freeing the lease until its expiry.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogReleaseFailed(logger, exception, request.LeaseId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The limit store is unavailable (failure mode {Mode}).")]
    private static partial void LogUnavailable(ILogger logger, Exception exception, LimitsFailureMode mode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Releasing the Concurrency lease of request {RequestId} failed; it will expire by itself.")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception, Guid requestId);
}
