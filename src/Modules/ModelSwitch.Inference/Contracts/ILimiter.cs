using ModelSwitch.SharedKernel;

namespace ModelSwitch.Inference.Contracts;

/// <summary>What a request asks the limiter for (architecture.md section 5, step 6).</summary>
/// <param name="TenantId">The Tenant whose Rate Limit and Concurrency Limit apply.</param>
/// <param name="LeaseId">Identifies this request's Concurrency lease (the Inference Request ID).</param>
/// <param name="Alias">The Model Alias asked for. Its Routing Policy's Deadline sizes the lease.</param>
/// <param name="Timeout">The client's <c>X-ModelSwitch-Timeout-Ms</c>, which can only shorten the Deadline.</param>
public sealed record LimitRequest(Guid TenantId, Guid LeaseId, string Alias, TimeSpan? Timeout);

/// <summary>Rate Limit state after admitting a request, for the <c>X-RateLimit-*</c> response headers (api.md section 1).</summary>
/// <param name="Limit">Requests per minute.</param>
/// <param name="Remaining">Requests that could start right now.</param>
/// <param name="Reset">Time until the full allowance is available again.</param>
public sealed record RateLimitState(int Limit, int Remaining, TimeSpan Reset);

/// <summary>
/// An admitted request's hold on its Tenant's Concurrency Limit. Dispose it when the request ends, whatever the outcome (also on
/// abort), and at most once is effective. A lease that is never released expires by itself, so a crashed host cannot leak it.
/// </summary>
public sealed class LimitLease : IAsyncDisposable
{
    private Func<ValueTask>? _release;

    internal LimitLease(RateLimitState? rate, Func<ValueTask>? release)
    {
        Rate = rate;
        _release = release;
    }

    /// <summary>Null when the Tenant has no Rate Limit, or when limits were skipped because Valkey is unavailable (fail-open).</summary>
    public RateLimitState? Rate { get; }

    /// <summary>A lease that holds nothing: the Tenant is unlimited, or the limiter failed open.</summary>
    internal static LimitLease Unlimited(RateLimitState? rate = null) => new(rate, release: null);

    public ValueTask DisposeAsync()
    {
        var release = Interlocked.Exchange(ref _release, null);
        return release is null ? ValueTask.CompletedTask : release();
    }
}

/// <summary>
/// Step 6: takes a Rate Limit token and a Concurrency lease for a request in one atomic Valkey call (ADR-0003). Failures are
/// <c>rate_limit_exceeded</c> and <c>concurrency_limit_exceeded</c> (429, with <c>Retry-After</c>) or, when Valkey is unreachable and
/// <c>Limits:FailureMode</c> is <c>Closed</c>, <c>limits_unavailable</c> (503). Applies to Request Origin <c>api</c> only.
/// </summary>
public interface ILimiter
{
    Task<Result<LimitLease>> AcquireAsync(LimitRequest request, CancellationToken cancellationToken);
}
