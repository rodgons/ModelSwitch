using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Routing.Contracts;

namespace ModelSwitch.Inference.Application;

/// <summary>What the failure-classification table allows after a failed Attempt (routing-and-resilience.md section 3).</summary>
/// <param name="RetrySameModel">Another Attempt on the same Provider Model is safe.</param>
/// <param name="Fallback">The next Candidate may be tried.</param>
internal readonly record struct FailureDecision(bool RetrySameModel, bool Fallback);

/// <summary>
/// The pure decisions of the attempt loop: which failures may be retried or fallen back from, how long to wait, and whether
/// the wait fits the Deadline. No clock, no randomness: the caller passes the jitter sample and the remaining time.
/// </summary>
internal static class RetryPolicy
{
    /// <summary>The longest <c>Retry-After</c> the gateway will wait for (routing-and-resilience.md section 3, HTTP 429).</summary>
    internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(5);

    /// <summary>The least time worth starting another Attempt for after a backoff wait.</summary>
    internal static readonly TimeSpan MinAttemptBudget = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Transient: retry and fall back. Ambiguous: never retry (the first call may have been billed), but fall back.
    /// Permanent: the request itself is the problem and nothing is retried or fallen back from, except Provider
    /// misconfiguration (401, 403, billing/quota), which says nothing about the next Provider, and
    /// <c>context_length_exceeded</c>, because the next model's window may be larger.
    /// </summary>
    public static FailureDecision Classify(ProviderFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.Class switch
        {
            FailureClass.Transient => new FailureDecision(RetrySameModel: true, Fallback: true),
            FailureClass.Ambiguous => new FailureDecision(RetrySameModel: false, Fallback: true),
            _ => new FailureDecision(
                RetrySameModel: false,
                Fallback: failure.Code is ProviderFailureCodes.Unauthorized
                    or ProviderFailureCodes.Forbidden
                    or ProviderFailureCodes.QuotaExhausted
                    or ProviderFailureCodes.ContextLengthExceeded),
        };
    }

    /// <summary>
    /// The exponential backoff before retry number <paramref name="retryIndex"/> (0 for the first retry): <c>base * 2^index</c>
    /// capped at the maximum. With full jitter the wait is a uniform sample from zero to that value, which spreads retries from
    /// many clients; <paramref name="jitter"/> is that sample in [0, 1).
    /// </summary>
    public static TimeSpan Backoff(BackoffSettings backoff, int retryIndex, double jitter)
    {
        ArgumentNullException.ThrowIfNull(backoff);
        var capped = Math.Min(backoff.MaxMs, backoff.BaseMs * Math.Pow(2, Math.Min(retryIndex, 30)));
        var milliseconds = backoff.FullJitter ? capped * Math.Clamp(jitter, 0, 1) : capped;
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    /// <summary>
    /// How long to wait before retrying the same Provider Model, or null when the failure must go to the next Candidate instead.
    /// A <c>Retry-After</c> is honoured only when it is at most <see cref="MaxRetryAfter"/> and leaves room for a whole
    /// Attempt: <c>retryAfter &lt;= remaining - attemptTimeout</c>. Otherwise the backoff applies, and only when a minimal
    /// Attempt still fits after it.
    /// </summary>
    /// <param name="failure">The failed Attempt's failure.</param>
    /// <param name="backoff">The policy's backoff.</param>
    /// <param name="retryIndex">0 for the first retry of this Candidate.</param>
    /// <param name="jitter">A uniform sample in [0, 1).</param>
    /// <param name="remaining">Time left until the Deadline.</param>
    /// <param name="attemptTimeout">The policy's per-Attempt timeout.</param>
    public static TimeSpan? DelayBeforeRetry(ProviderFailure failure, BackoffSettings backoff, int retryIndex, double jitter, TimeSpan remaining, TimeSpan attemptTimeout)
    {
        if (!Classify(failure).RetrySameModel)
        {
            return null;
        }

        if (failure.RetryAfter is { } retryAfter)
        {
            return retryAfter <= MaxRetryAfter && retryAfter <= remaining - attemptTimeout ? retryAfter : null;
        }

        var delay = Backoff(backoff, retryIndex, jitter);
        return delay + MinAttemptBudget <= remaining ? delay : null;
    }
}
