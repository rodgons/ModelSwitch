namespace ModelSwitch.SharedKernel.Jobs;

/// <summary>What happens to a job after a failed attempt.</summary>
internal readonly record struct FailureDecision(bool Dead, DateTimeOffset RunAfter);

/// <summary>Pure retry, backoff, lease and slot arithmetic, so the rules are testable without a database.</summary>
internal static class JobRetryPolicy
{
    /// <summary>Exponential backoff: <c>BackoffBase * 2^(attempts-1)</c>, capped at <c>BackoffMax</c>. No jitter: jobs are claimed one at a time.</summary>
    public static TimeSpan Backoff(int attempts, JobsOptions options)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 30);
        var ticks = options.BackoffBase.Ticks * (1L << exponent);
        return ticks <= 0 || ticks > options.BackoffMax.Ticks ? options.BackoffMax : TimeSpan.FromTicks(ticks);
    }

    /// <summary>
    /// A failure on attempt <paramref name="attempts"/> (1-based). The job dies when it was permanent or that was the last allowed attempt.
    /// </summary>
    public static FailureDecision AfterFailure(int attempts, int maxAttempts, bool permanent, DateTimeOffset now, JobsOptions options)
        => permanent || attempts >= maxAttempts
            ? new FailureDecision(true, now)
            : new FailureDecision(false, now + Backoff(attempts, options));

    public static DateTimeOffset LeaseUntil(DateTimeOffset now, JobsOptions options) => now + options.LeaseDuration;

    /// <summary>True once the lease has passed, so the job may be reclaimed. The claim SQL applies the same comparison.</summary>
    public static bool LeaseExpired(DateTimeOffset lockedUntil, DateTimeOffset now) => lockedUntil < now;

    /// <summary>The interval containing <paramref name="now"/>, shared by every replica (it is the dedupe key's suffix).</summary>
    public static long Slot(DateTimeOffset now, TimeSpan interval) => now.UtcTicks / interval.Ticks;

    public static DateTimeOffset SlotStart(long slot, TimeSpan interval) => new(slot * interval.Ticks, TimeSpan.Zero);
}
