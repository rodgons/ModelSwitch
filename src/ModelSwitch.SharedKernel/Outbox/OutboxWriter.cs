using System.Data.Common;

namespace ModelSwitch.SharedKernel.Outbox;

/// <summary>
/// The write side of the transactional outbox (architecture.md section 3, data-model.md "Background processing"). A module
/// appends its event on its own connection and transaction, so the event commits or rolls back with the change that caused it.
/// The relay that publishes <c>jobs.outbox_event</c> rows belongs to the worker's jobs infrastructure.
/// </summary>
public static class OutboxWriter
{
    /// <summary>The outbox table (the <c>jobs</c> schema).</summary>
    public const string Table = "jobs.outbox_event";

    /// <summary>Emitted when an Inference Request has been settled (usage-and-budgets.md section 6).</summary>
    public const string UsageRequestCompleted = "usage.request_completed";

    /// <summary>
    /// Inserts one unpublished event. <paramref name="payloadJson"/> must be metadata only, never prompt or completion content (ADR-0010).
    /// </summary>
    public static async Task AppendAsync(
        DbConnection connection,
        DbTransaction? transaction,
        Guid id,
        string type,
        string payloadJson,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(payloadJson);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {Table} (id, type, payload, occurred_at) VALUES (@id, @type, @payload::jsonb, @at)";
        Add(command, "id", id);
        Add(command, "type", type);
        Add(command, "payload", payloadJson);
        Add(command, "at", occurredAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
