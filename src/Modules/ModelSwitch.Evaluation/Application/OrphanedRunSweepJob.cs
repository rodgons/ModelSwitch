using Microsoft.EntityFrameworkCore;
using ModelSwitch.Evaluation.Domain.Runs;
using ModelSwitch.Evaluation.Infrastructure;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Jobs;

namespace ModelSwitch.Evaluation.Application;

/// <summary>
/// The recurring <c>evaluation.sweep_orphaned_runs</c> job (evaluation.md section 5). A worker killed during an <c>evaluation.run</c>
/// job's last attempt leaves the job <c>dead</c> (the claim loop dead-letters it once the lease expires) without the handler ever
/// marking the run, so the run would stay <c>queued</c> or <c>running</c> forever. Each pass reads the unfinished runs, looks up their
/// jobs by dedupe key and fails every orphan (<see cref="RunStatusRules.IsOrphaned"/>) with <c>internal_error</c>, its aggregates from
/// the stored Case Results and <c>completed_at</c>, through the same guarded update as the handler's own last-attempt failure, so a
/// run that finishes meanwhile is left as it is. It also reads the runs cancelled while running that have no <c>completed_at</c>:
/// when their worker died before storing the aggregates (<see cref="RunStatusRules.NeedsCancelledFinalization"/>), it stores them and
/// <c>completed_at</c>, and they stay <c>cancelled</c>. Idempotent, so a lease handed to another worker mid-pass is harmless.
/// </summary>
internal sealed class OrphanedRunSweepJob(IDbContextFactory<EvaluationDbContext> contexts, IJobStatusReader jobs, RunExecutor executor) : IJobHandler
{
    public const string JobKind = "evaluation.sweep_orphaned_runs";

    /// <summary>A dead job is reaped at the earliest one lease (60 s) after its worker died, so a minute is soon enough.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private const int _batchSize = 500;

    public string Kind => JobKind;

    public async Task HandleAsync(JobContext context, CancellationToken cancellationToken)
    {
        // Unfinished runs are few (each one is a run someone started and is waiting on), so one read holds them all.
        List<(Guid Id, string Status, bool Completed)> unfinished;
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            var rows = await db.Runs.AsNoTracking()
                .Where(r => r.Status == EvaluationRunStatuses.Queued
                    || r.Status == EvaluationRunStatuses.Running
                    || (r.Status == EvaluationRunStatuses.Cancelled && r.CompletedAt == null))
                .Select(r => new { r.Id, r.Status, Completed = r.CompletedAt != null })
                .ToListAsync(cancellationToken);
            unfinished = [.. rows.Select(r => (r.Id, r.Status, r.Completed))];
        }

        foreach (var batch in unfinished.Chunk(_batchSize))
        {
            var statuses = await jobs.StatusesByDedupeKeyAsync([.. batch.Select(r => EvaluationRunJob.DedupeKey(r.Id))], cancellationToken);
            foreach (var (id, status, completed) in batch)
            {
                var jobStatus = statuses.GetValueOrDefault(EvaluationRunJob.DedupeKey(id));
                if (RunStatusRules.IsOrphaned(status, jobStatus))
                {
                    await executor.FailAsync(id, ErrorCode.InternalError.Value, cancellationToken);
                }
                else if (RunStatusRules.NeedsCancelledFinalization(status, completed, jobStatus))
                {
                    await executor.FinalizeCancelledAsync(id, cancellationToken);
                }
            }
        }
    }
}
