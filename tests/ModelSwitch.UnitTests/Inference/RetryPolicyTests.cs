using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Inference.Application;
using ModelSwitch.Routing.Contracts;
using Shouldly;

namespace ModelSwitch.UnitTests.Inference;

public sealed class RetryPolicyTests
{
    private static readonly BackoffSettings _backoff = new(200, 2_000, FullJitter: true);

    private static ProviderFailure Transient(TimeSpan? retryAfter = null)
        => new(FailureClass.Transient, ProviderFailureCodes.RateLimited, 429, retryAfter, RequestSent: true);

    [Theory]
    [InlineData(0, 0.0, 0)]
    [InlineData(0, 0.5, 100)]
    [InlineData(1, 0.5, 200)]
    [InlineData(3, 1.0, 1600)]
    [InlineData(4, 0.5, 1000)]
    [InlineData(9, 0.999, 1998)]
    [InlineData(40, 0.5, 1000)]
    public void Full_jitter_backoff_is_a_fraction_of_the_capped_exponential(int retryIndex, double jitter, double expectedMs)
        => RetryPolicy.Backoff(_backoff, retryIndex, jitter).TotalMilliseconds.ShouldBe(expectedMs, tolerance: 0.5);

    [Theory]
    [InlineData(0, 200)]
    [InlineData(1, 400)]
    [InlineData(3, 1600)]
    [InlineData(4, 2000)]
    [InlineData(20, 2000)]
    public void Backoff_without_jitter_doubles_up_to_the_maximum(int retryIndex, double expectedMs)
        => RetryPolicy.Backoff(_backoff with { FullJitter = false }, retryIndex, jitter: 0.1).TotalMilliseconds.ShouldBe(expectedMs);

    [Fact]
    public void Backoff_never_exceeds_the_cap_whatever_the_jitter()
    {
        for (var retryIndex = 0; retryIndex < 12; retryIndex++)
        {
            var cap = Math.Min(2_000, 200 * Math.Pow(2, retryIndex));
            foreach (var jitter in new[] { 0.0, 0.25, 0.5, 0.75, 0.999999 })
            {
                RetryPolicy.Backoff(_backoff, retryIndex, jitter).TotalMilliseconds.ShouldBeInRange(0, cap);
            }
        }
    }

    // remaining and attemptTimeout in seconds. A Retry-After is honoured only when it is at most 5 s and
    // retryAfter <= remaining - attemptTimeout (routing-and-resilience.md section 3, HTTP 429).
    [Theory]
    [InlineData(1, 60, 30, 1.0)]
    [InlineData(0, 60, 30, 0.0)]
    [InlineData(5, 60, 30, 5.0)]
    [InlineData(6, 60, 30, null)]
    [InlineData(120, 600, 30, null)]
    [InlineData(2, 32, 30, 2.0)]
    [InlineData(2, 31.9, 30, null)]
    [InlineData(1, 30, 30, null)]
    public void Retry_after_is_honoured_only_when_it_fits_the_deadline_and_the_cap(double retryAfterSeconds, double remainingSeconds, double attemptSeconds, double? expectedSeconds)
    {
        var delay = RetryPolicy.DelayBeforeRetry(
            Transient(TimeSpan.FromSeconds(retryAfterSeconds)),
            _backoff,
            retryIndex: 0,
            jitter: 0.9,
            TimeSpan.FromSeconds(remainingSeconds),
            TimeSpan.FromSeconds(attemptSeconds));

        (delay?.TotalSeconds).ShouldBe(expectedSeconds);
    }

    [Fact]
    public void Without_retry_after_the_backoff_applies_when_a_minimal_attempt_still_fits()
    {
        var failure = Transient();

        RetryPolicy.DelayBeforeRetry(failure, _backoff, 0, 0.5, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)).ShouldBe(TimeSpan.FromMilliseconds(100));
        RetryPolicy.DelayBeforeRetry(failure, _backoff, 0, 0.5, TimeSpan.FromMilliseconds(599), TimeSpan.FromSeconds(30)).ShouldBeNull();
        RetryPolicy.DelayBeforeRetry(failure, _backoff, 0, 0.5, TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(30)).ShouldBe(TimeSpan.FromMilliseconds(100));
    }

    [Theory]
    [InlineData(FailureClass.Ambiguous)]
    [InlineData(FailureClass.Permanent)]
    public void Ambiguous_and_permanent_failures_are_never_retried_on_the_same_model_even_with_retry_after(FailureClass failureClass)
    {
        var failure = new ProviderFailure(failureClass, ProviderFailureCodes.Timeout, null, TimeSpan.FromSeconds(1), RequestSent: true);

        RetryPolicy.DelayBeforeRetry(failure, _backoff, 0, 0.5, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1)).ShouldBeNull();
    }
}
