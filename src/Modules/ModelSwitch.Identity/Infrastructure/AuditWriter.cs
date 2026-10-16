using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ModelSwitch.Identity.Application;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel;
using Npgsql;
using NpgsqlTypes;

namespace ModelSwitch.Identity.Infrastructure;

/// <summary>
/// Inserts Audit Records with plain SQL so the same code serves the caller's own transaction and a transaction of its own.
/// The application role has INSERT and SELECT on the table but no UPDATE or DELETE (data-model.md, DatabaseRoles).
/// </summary>
internal sealed class AuditWriter(
    IDbContextFactory<IdentityDbContext> contexts,
    IAuditCallContext callContext,
    IGuidGenerator ids,
    TimeProvider time) : IAuditWriter
{
    private const string _insert = """
        INSERT INTO identity.audit_record
            (id, occurred_at, administrator_id, action, tenant_id, target_type, target_id, details, source_ip, request_id, trace_id)
        VALUES
            (@id, @occurred_at, @administrator_id, @action, @tenant_id, @target_type, @target_id, @details, @source_ip, @request_id, @trace_id)
        """;

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await WriteAsync(entry, transaction.GetDbTransaction(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task WriteAsync(AuditEntry entry, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.Connection is not NpgsqlConnection connection)
        {
            throw new ArgumentException("The Audit Record transaction must be a PostgreSQL transaction.", nameof(transaction));
        }

        var record = AuditRecordFactory.Create(entry, callContext.Capture(), ids.NewGuid(), time.GetUtcNow());
        await using var command = new NpgsqlCommand(_insert, connection, (NpgsqlTransaction)transaction);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = record.Id });
        command.Parameters.Add(new NpgsqlParameter("occurred_at", NpgsqlDbType.TimestampTz) { Value = record.OccurredAt.UtcDateTime });
        command.Parameters.Add(new NpgsqlParameter("administrator_id", NpgsqlDbType.Uuid) { Value = (object?)record.AdministratorId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("action", NpgsqlDbType.Text) { Value = record.Action });
        command.Parameters.Add(new NpgsqlParameter("tenant_id", NpgsqlDbType.Uuid) { Value = (object?)record.TenantId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("target_type", NpgsqlDbType.Text) { Value = record.TargetType });
        command.Parameters.Add(new NpgsqlParameter("target_id", NpgsqlDbType.Text) { Value = (object?)record.TargetId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("details", NpgsqlDbType.Jsonb) { Value = record.Details });
        command.Parameters.Add(new NpgsqlParameter("source_ip", NpgsqlDbType.Inet) { Value = (object?)record.SourceIp ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("request_id", NpgsqlDbType.Uuid) { Value = (object?)record.RequestId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("trace_id", NpgsqlDbType.Text) { Value = (object?)record.TraceId ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
