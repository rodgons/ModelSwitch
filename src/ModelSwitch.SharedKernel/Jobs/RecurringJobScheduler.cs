namespace ModelSwitch.SharedKernel.Jobs;

/// <summary>
/// Keeps recurring jobs alive across any number of worker replicas. Every replica tries to enqueue the job for the current time slot
/// (<c>floor(now / interval)</c>) under the dedupe key <c>kind@slot</c>; the unique index lets exactly one insert win, and that job is then
/// claimed like any other (lease, retry, dead-letter). A slot missed while every worker was down is skipped, not replayed.
/// </summary>
internal sealed class RecurringJobScheduler(IJobEnqueuer queue, IEnumerable<RecurringJobSchedule> schedules, TimeProvider time)
{
    private readonly RecurringJobSchedule[] _schedules = [.. schedules];
    private readonly Dictionary<string, long> _lastSlot = [];

    /// <summary>Enqueues the job of each schedule whose slot has not been attempted by this replica yet. Returns how many this call inserted.</summary>
    public async Task<int> ScheduleDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var inserted = 0;
        foreach (var schedule in _schedules)
        {
            var slot = JobRetryPolicy.Slot(now, schedule.Interval);
            if (_lastSlot.TryGetValue(schedule.Kind, out var last) && last == slot)
            {
                continue;
            }

            var id = await queue.EnqueueAsync(
                new JobRequest(
                    schedule.Kind,
                    RunAfter: JobRetryPolicy.SlotStart(slot, schedule.Interval),
                    DedupeKey: $"{schedule.Kind}@{slot}",
                    UniqueWhileActive: true),
                cancellationToken);
            _lastSlot[schedule.Kind] = slot;
            if (id is not null)
            {
                inserted++;
            }
        }

        return inserted;
    }
}
