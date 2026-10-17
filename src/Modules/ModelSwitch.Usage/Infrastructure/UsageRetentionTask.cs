using ModelSwitch.SharedKernel.Retention;

namespace ModelSwitch.Usage.Infrastructure;

/// <summary>
/// Retention of the <c>usage</c> schema (data-model.md, "Retention"). Request and Attempt rows go by whole monthly partitions once a
/// partition's month has fully expired, plus a batched delete for the partition that straddles the cutoff. Usage Records and the
/// hourly rollup are kept far longer, so the dashboard's history outlives the request rows. Settled and expired Reservations go
/// with the request rows; held ones are left to the sweeper.
/// </summary>
internal sealed class UsageRetentionTask : IRetentionTask
{
    public string Name => "usage";

    public async Task<long> RunAsync(RetentionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var source = context.Source.DataSource;
        var options = context.Options;
        var cutoffs = context.Cutoffs;

        // Creates next months' partitions and drops expired ones, as the migrator role (the worker's roles cannot run DDL).
        long removed = 0;
        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT count(*) FROM usage.maintain_request_partitions(@days, @ahead)";
            var days = command.CreateParameter();
            days.ParameterName = "days";
            days.Value = options.RequestDays;
            command.Parameters.Add(days);
            var ahead = command.CreateParameter();
            ahead.ParameterName = "ahead";
            ahead.Value = options.PartitionMonthsAhead;
            command.Parameters.Add(ahead);
            removed += Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }

        var batch = options.BatchSize;
        removed += await RetentionSql.DeleteInBatchesAsync(source, "usage.provider_attempt", "id, started_at", "started_at < @cutoff", cutoffs.Requests, batch, cancellationToken);
        removed += await RetentionSql.DeleteInBatchesAsync(source, "usage.inference_request", "id, started_at", "started_at < @cutoff AND status <> 'running'", cutoffs.Requests, batch, cancellationToken);
        removed += await RetentionSql.DeleteInBatchesAsync(source, "usage.usage_record", "id", "recorded_at < @cutoff", cutoffs.Usage, batch, cancellationToken);

        // A finished Reservation is kept as long as the request rows it points to; a held one is the sweeper's, never retention's.
        removed += await RetentionSql.DeleteInBatchesAsync(source, "usage.reservation", "id", "status <> 'held' AND settled_at < @cutoff", cutoffs.Reservations, batch, cancellationToken);
        removed += await RetentionSql.DeleteInBatchesAsync(source, "usage.usage_hourly", "tenant_id, provider_model_id, requested_alias, origin, hour", "hour < @cutoff", cutoffs.Usage, batch, cancellationToken);
        return removed;
    }
}
