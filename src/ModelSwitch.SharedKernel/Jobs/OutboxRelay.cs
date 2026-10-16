using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ModelSwitch.SharedKernel.Jobs;

/// <summary>
/// Delivers <c>jobs.outbox_event</c> rows to the registered <see cref="IOutboxConsumer"/>s (data-model.md "Background processing").
/// Each event is handled in its own transaction that holds the row lock (<c>FOR UPDATE SKIP LOCKED</c>, so replicas never relay the same
/// event together), records one <c>processed_event</c> row per consumer before calling it, and marks the event published. Redelivery
/// after a crash is therefore a no-op for every consumer that already committed (NFR-06). A throwing consumer rolls the event back
/// and delays it with exponential backoff, without blocking the events behind it. Events whose type has no consumer are left
/// unpublished, so a consumer added later (the Phase 4 usage rollup) still sees the backlog.
/// </summary>
internal sealed partial class OutboxRelay
{
    private readonly DbDataSource _dataSource;
    private readonly IReadOnlyList<IOutboxConsumer> _consumers;
    private readonly string[] _types;
    private readonly TimeProvider _time;
    private readonly JobsOptions _options;
    private readonly ILogger<OutboxRelay> _logger;

    public OutboxRelay(
        DbDataSource dataSource,
        IEnumerable<IOutboxConsumer> consumers,
        TimeProvider time,
        IOptions<JobsOptions> options,
        ILogger<OutboxRelay> logger)
    {
        _dataSource = dataSource;
        _consumers = [.. consumers];
        _types = [.. _consumers.Select(c => c.EventType).Distinct(StringComparer.Ordinal)];
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Relays up to <paramref name="batchSize"/> events and returns how many were published.</summary>
    public async Task<int> RelayAsync(int batchSize, CancellationToken cancellationToken)
    {
        if (_types.Length == 0)
        {
            return 0;
        }

        var published = 0;
        for (var handled = 0; handled < batchSize; handled++)
        {
            var outcome = await RelayOneAsync(cancellationToken);
            if (outcome is null)
            {
                break;
            }

            if (outcome is true)
            {
                published++;
            }
        }

        return published;
    }

    // Returns null when no event is due, true when one was published and false when one failed (so the pass goes on to the next).
    private async Task<bool?> RelayOneAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        OutboxEvent? next;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT id, type, payload::text, occurred_at FROM jobs.outbox_event
                WHERE published_at IS NULL AND type = ANY(@types) AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
                ORDER BY occurred_at, id
                FOR UPDATE SKIP LOCKED
                LIMIT 1
                """;
            JobSql.Add(select, "types", _types);
            JobSql.Add(select, "now", now);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            next = await reader.ReadAsync(cancellationToken)
                ? new OutboxEvent(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3))
                : null;
        }

        if (next is not { } outboxEvent)
        {
            return null;
        }

        try
        {
            foreach (var consumer in _consumers.Where(c => c.EventType == outboxEvent.Type))
            {
                if (await MarkProcessedAsync(connection, transaction, consumer.Name, outboxEvent.Id, now, cancellationToken))
                {
                    await consumer.HandleAsync(outboxEvent, connection, transaction, cancellationToken);
                }
            }

            await using var publish = connection.CreateCommand();
            publish.Transaction = transaction;
            publish.CommandText = "UPDATE jobs.outbox_event SET published_at = @now, next_attempt_at = NULL WHERE id = @id";
            JobSql.Add(publish, "now", now);
            JobSql.Add(publish, "id", outboxEvent.Id);
            await publish.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // CancellationToken.None is deliberate: the rollback and the failure record must finish even while the host shuts down.
            await transaction.RollbackAsync(CancellationToken.None);
            var attempt = await RecordFailureAsync(outboxEvent.Id, exception, now);
            LogConsumerFailed(_logger, exception, outboxEvent.Type, outboxEvent.Id, attempt);
            return false;
        }
    }

    private static async Task<bool> MarkProcessedAsync(DbConnection connection, DbTransaction transaction, string consumer, Guid eventId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO jobs.processed_event (consumer, event_id, processed_at) VALUES (@consumer, @event, @now) ON CONFLICT DO NOTHING";
        JobSql.Add(command, "consumer", consumer, DbType.String);
        JobSql.Add(command, "event", eventId);
        JobSql.Add(command, "now", now);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    // Runs on a fresh connection because the failed transaction is gone. Returns the attempt number that just failed.
    private async Task<int> RecordFailureAsync(Guid eventId, Exception exception, DateTimeOffset now)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT attempts FROM jobs.outbox_event WHERE id = @id";
        JobSql.Add(read, "id", eventId);
        var attempts = Convert.ToInt32(await read.ExecuteScalarAsync(CancellationToken.None) ?? 0, System.Globalization.CultureInfo.InvariantCulture) + 1;

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs.outbox_event SET attempts = attempts + 1, last_error = @error, next_attempt_at = @next WHERE id = @id";
        JobSql.Add(command, "error", JobSql.DescribeError(exception), DbType.String);
        JobSql.Add(command, "next", now + JobRetryPolicy.Backoff(attempts, _options));
        JobSql.Add(command, "id", eventId);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
        return attempts;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox event {EventType} {EventId} failed in a consumer (attempt {Attempt}); it will be retried with backoff.")]
    private static partial void LogConsumerFailed(ILogger logger, Exception exception, string eventType, Guid eventId, int attempt);
}
