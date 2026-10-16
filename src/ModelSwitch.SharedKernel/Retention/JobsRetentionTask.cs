namespace ModelSwitch.SharedKernel.Retention;

/// <summary>Retention of the shared <c>jobs</c> schema: published outbox events and their idempotency rows after 7 days, ended jobs after 14.</summary>
internal sealed class JobsRetentionTask : IRetentionTask
{
    public string Name => "jobs";

    public async Task<long> RunAsync(RetentionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var source = context.Source.DataSource;
        var batch = context.Options.BatchSize;
        var cutoffs = context.Cutoffs;
        return await RetentionSql.DeleteInBatchesAsync(source, "jobs.outbox_event", "id", "published_at < @cutoff", cutoffs.Outbox, batch, cancellationToken)
            + await RetentionSql.DeleteInBatchesAsync(source, "jobs.processed_event", "consumer, event_id", "processed_at < @cutoff", cutoffs.Outbox, batch, cancellationToken)
            + await RetentionSql.DeleteInBatchesAsync(source, "jobs.job", "id", "status IN ('succeeded', 'dead') AND updated_at < @cutoff", cutoffs.Jobs, batch, cancellationToken);
    }
}
