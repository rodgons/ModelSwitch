using Microsoft.Extensions.Time.Testing;
using ModelSwitch.SharedKernel.Jobs;
using Shouldly;

namespace ModelSwitch.UnitTests.Jobs;

public sealed class JobRetryPolicyTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly JobsOptions _options = new() { BackoffBase = TimeSpan.FromSeconds(5), BackoffMax = TimeSpan.FromMinutes(1), LeaseDuration = TimeSpan.FromSeconds(60) };

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(5, 60)]
    [InlineData(40, 60)]
    public void Backoff_doubles_per_attempt_up_to_the_cap(int attempts, int expectedSeconds)
        => JobRetryPolicy.Backoff(attempts, _options).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));

    [Fact]
    public void A_failed_attempt_below_the_limit_is_retried_after_the_backoff()
    {
        var decision = JobRetryPolicy.AfterFailure(attempts: 2, maxAttempts: 5, permanent: false, _time.GetUtcNow(), _options);

        decision.Dead.ShouldBeFalse();
        decision.RunAfter.ShouldBe(_time.GetUtcNow().AddSeconds(10));
    }

    [Fact]
    public void The_failure_of_the_last_allowed_attempt_dead_letters_the_job()
    {
        var decision = JobRetryPolicy.AfterFailure(attempts: 5, maxAttempts: 5, permanent: false, _time.GetUtcNow(), _options);

        decision.Dead.ShouldBeTrue();
    }

    [Fact]
    public void A_permanent_failure_dead_letters_the_job_on_its_first_attempt()
    {
        JobRetryPolicy.AfterFailure(attempts: 1, maxAttempts: 5, permanent: true, _time.GetUtcNow(), _options).Dead.ShouldBeTrue();
    }

    [Fact]
    public void A_lease_is_valid_until_its_expiry_and_reclaimable_after()
    {
        var until = JobRetryPolicy.LeaseUntil(_time.GetUtcNow(), _options);
        until.ShouldBe(_time.GetUtcNow().AddSeconds(60));

        _time.Advance(TimeSpan.FromSeconds(60));
        JobRetryPolicy.LeaseExpired(until, _time.GetUtcNow()).ShouldBeFalse();

        _time.Advance(TimeSpan.FromMilliseconds(1));
        JobRetryPolicy.LeaseExpired(until, _time.GetUtcNow()).ShouldBeTrue();
    }

    [Fact]
    public void Every_replica_computes_the_same_slot_for_the_same_instant()
    {
        var interval = TimeSpan.FromSeconds(30);
        var slot = JobRetryPolicy.Slot(_time.GetUtcNow(), interval);

        JobRetryPolicy.Slot(_time.GetUtcNow().AddSeconds(29), interval).ShouldBe(slot);
        JobRetryPolicy.Slot(_time.GetUtcNow().AddSeconds(30), interval).ShouldBe(slot + 1);
        JobRetryPolicy.SlotStart(slot, interval).ShouldBe(_time.GetUtcNow()); // 12:00:00 is on a 30 s boundary
    }
}

public sealed class RecurringJobSchedulerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));

    private sealed class RecordingQueue : IJobEnqueuer
    {
        public List<JobRequest> Requests { get; } = [];

        public Task<Guid?> EnqueueAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction, JobRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Guid?> EnqueueAsync(JobRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult<Guid?>(Guid.CreateVersion7());
        }
    }

    [Fact]
    public async Task It_enqueues_once_per_slot_with_a_slot_scoped_dedupe_key()
    {
        var queue = new RecordingQueue();
        var scheduler = new RecurringJobScheduler(queue, [new RecurringJobSchedule("usage.sweep_reservations", TimeSpan.FromSeconds(30))], _time);

        (await scheduler.ScheduleDueAsync(CancellationToken.None)).ShouldBe(1);
        _time.Advance(TimeSpan.FromSeconds(10));
        (await scheduler.ScheduleDueAsync(CancellationToken.None)).ShouldBe(0);
        _time.Advance(TimeSpan.FromSeconds(20));
        (await scheduler.ScheduleDueAsync(CancellationToken.None)).ShouldBe(1);

        queue.Requests.Count.ShouldBe(2);
        queue.Requests[0].DedupeKey.ShouldNotBe(queue.Requests[1].DedupeKey);
        queue.Requests.ShouldAllBe(r => r.UniqueWhileActive && r.Kind == "usage.sweep_reservations");
    }
}
