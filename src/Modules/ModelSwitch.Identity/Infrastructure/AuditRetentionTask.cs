using ModelSwitch.SharedKernel.Retention;

namespace ModelSwitch.Identity.Infrastructure;

/// <summary>
/// Retention of Audit Records (data-model.md, "Retention"): after 400 days, and only as the dedicated retention role. The app role can
/// never delete them (they are append-only for it), so without the retention role this task deletes nothing.
/// </summary>
internal sealed class AuditRetentionTask : IRetentionTask
{
    public string Name => "audit";

    public async Task<long> RunAsync(RetentionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Source.IsRetentionRole
            ? await RetentionSql.DeleteInBatchesAsync(
                context.Source.DataSource,
                "identity.audit_record",
                "id",
                "occurred_at < @cutoff",
                context.Cutoffs.Audit,
                context.Options.BatchSize,
                cancellationToken)
            : 0;
    }
}
