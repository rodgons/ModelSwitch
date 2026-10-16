using System.Data.Common;
using ModelSwitch.SharedKernel.Persistence;

namespace ModelSwitch.SharedKernel.Jobs;

/// <summary>
/// Creates the shared <c>jobs</c> schema with plain idempotent DDL (no entity model): <c>outbox_event</c>, <c>job</c> and
/// <c>processed_event</c> (data-model.md "Background processing"). The Migrator is the only host that runs it.
/// </summary>
public sealed class JobsSchemaMigrator(DbDataSource dataSource) : IModuleMigrator
{
    public const string SchemaName = "jobs";

    private static readonly string[] _tables = ["outbox_event", "job", "processed_event"];

    public string Schema => SchemaName;

    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(hashtext('modelswitch.jobs.migrate'))", cancellationToken);

        var missing = new List<string>();
        foreach (var table in _tables)
        {
            await using var exists = connection.CreateCommand();
            exists.Transaction = transaction;
            exists.CommandText = $"SELECT to_regclass('jobs.{table}') IS NOT NULL";
            if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken))!)
            {
                missing.Add(table);
            }
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            CREATE SCHEMA IF NOT EXISTS jobs;

            CREATE TABLE IF NOT EXISTS jobs.outbox_event (
                id uuid PRIMARY KEY,
                type text NOT NULL,
                payload jsonb NOT NULL,
                occurred_at timestamptz NOT NULL,
                published_at timestamptz NULL
            );
            ALTER TABLE jobs.outbox_event ADD COLUMN IF NOT EXISTS attempts integer NOT NULL DEFAULT 0;
            ALTER TABLE jobs.outbox_event ADD COLUMN IF NOT EXISTS next_attempt_at timestamptz NULL;
            ALTER TABLE jobs.outbox_event ADD COLUMN IF NOT EXISTS last_error text NULL;
            CREATE INDEX IF NOT EXISTS ix_outbox_event_unpublished ON jobs.outbox_event (published_at) WHERE published_at IS NULL;

            CREATE TABLE IF NOT EXISTS jobs.processed_event (
                consumer text NOT NULL,
                event_id uuid NOT NULL,
                processed_at timestamptz NOT NULL,
                PRIMARY KEY (consumer, event_id)
            );

            CREATE TABLE IF NOT EXISTS jobs.job (
                id uuid PRIMARY KEY,
                kind text NOT NULL,
                payload jsonb NOT NULL,
                status text NOT NULL CHECK (status IN ('pending', 'running', 'succeeded', 'dead')),
                attempts integer NOT NULL DEFAULT 0,
                max_attempts integer NOT NULL,
                run_after timestamptz NOT NULL,
                locked_by text NULL,
                locked_until timestamptz NULL,
                last_error text NULL,
                dedupe_key text NULL,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_job_pending ON jobs.job (run_after) WHERE status = 'pending';
            CREATE INDEX IF NOT EXISTS ix_job_lease ON jobs.job (locked_until) WHERE status = 'running';
            CREATE UNIQUE INDEX IF NOT EXISTS ux_job_dedupe_key ON jobs.job (dedupe_key) WHERE dedupe_key IS NOT NULL;
            """,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return missing;
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
