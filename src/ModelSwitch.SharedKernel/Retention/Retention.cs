using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace ModelSwitch.SharedKernel.Retention;

/// <summary>
/// How long each kind of data is kept (data-model.md, "Retention"). Only <c>Retention:RequestDays</c> is a deployment setting; the
/// other periods are the documented defaults.
/// </summary>
public sealed class RetentionOptions
{
    public const string RequestDaysSetting = "Retention:RequestDays";

    /// <summary><c>inference_request</c> and <c>provider_attempt</c>.</summary>
    public int RequestDays { get; init; } = 30;

    /// <summary><c>usage_record</c> and <c>usage_hourly</c>.</summary>
    public int UsageDays { get; init; } = 400;

    public int AuditDays { get; init; } = 400;

    /// <summary>Published <c>outbox_event</c> rows and <c>processed_event</c>.</summary>
    public int OutboxDays { get; init; } = 7;

    /// <summary>Jobs that ended (<c>succeeded</c> or <c>dead</c>).</summary>
    public int JobDays { get; init; } = 14;

    /// <summary>Rows per delete statement, so a large backlog never holds locks for long.</summary>
    public int BatchSize { get; init; } = 5_000;

    /// <summary>Months of request partitions kept ahead of the current one.</summary>
    public int PartitionMonthsAhead { get; init; } = 12;

    /// <summary>Reads <c>Retention:RequestDays</c> (1 to 400; anything else keeps the default of 30).</summary>
    public static RetentionOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var defaults = new RetentionOptions();
        return int.TryParse(configuration[RequestDaysSetting], NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= 400
            ? new RetentionOptions { RequestDays = days }
            : defaults;
    }

    /// <summary>The instants before which each kind of data is deleted, as of <paramref name="now"/>.</summary>
    public RetentionCutoffs CutoffsAt(DateTimeOffset now) => new(
        now.AddDays(-RequestDays),
        now.AddDays(-UsageDays),
        now.AddDays(-AuditDays),
        now.AddDays(-OutboxDays),
        now.AddDays(-JobDays));
}

/// <summary>Rows older than these instants are deleted.</summary>
public sealed record RetentionCutoffs(DateTimeOffset Requests, DateTimeOffset Usage, DateTimeOffset Audit, DateTimeOffset Outbox, DateTimeOffset Jobs);

/// <summary>
/// Where retention runs: the <c>modelswitch_retention</c> role when <c>ConnectionStrings:PostgresRetention</c> is set, otherwise the
/// app role, which may delete from every retention-managed table except <c>audit_record</c>.
/// </summary>
/// <param name="DataSource">Opens connections for the retention tasks.</param>
/// <param name="IsRetentionRole">False when running as the app role: append-only tables are then left alone.</param>
public sealed record RetentionDataSource(DbDataSource DataSource, bool IsRetentionRole);

/// <summary>What one retention task is given for a run.</summary>
public sealed record RetentionContext(RetentionDataSource Source, RetentionOptions Options, RetentionCutoffs Cutoffs);

/// <summary>
/// One module's part of retention: it deletes from its own tables only, in batches, and returns the rows (or partitions) removed.
/// Tasks are independent: one that fails does not stop the others.
/// </summary>
public interface IRetentionTask
{
    string Name { get; }

    Task<long> RunAsync(RetentionContext context, CancellationToken cancellationToken);
}

/// <summary>The batched delete every retention task uses.</summary>
public static class RetentionSql
{
    /// <summary>
    /// Deletes rows of <paramref name="table"/> matching <paramref name="condition"/> (which may use <c>@cutoff</c>) in batches of
    /// <paramref name="batchSize"/>, each its own statement. Batches are selected by <paramref name="key"/>, the table's primary key, so
    /// the statement is right on partitioned tables too (a <c>ctid</c> is only unique within one partition).
    /// </summary>
    public static async Task<long> DeleteInBatchesAsync(
        DbDataSource dataSource,
        string table,
        string key,
        string condition,
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        long total = 0;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        while (true)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM {table} WHERE ({key}) IN (SELECT {key} FROM {table} WHERE {condition} LIMIT {batchSize.ToString(CultureInfo.InvariantCulture)})";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "cutoff";
            parameter.Value = cutoff;
            command.Parameters.Add(parameter);
            var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
            total += deleted;
            if (deleted < batchSize)
            {
                return total;
            }
        }
    }
}
