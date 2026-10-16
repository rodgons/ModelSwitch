using ModelSwitch.SharedKernel.Persistence;
using Npgsql;

namespace ModelSwitch.Configuration.Infrastructure;

/// <summary>
/// Creates the shared <c>jobs</c> schema with plain idempotent DDL (no entity model). Today it holds <c>outbox_event</c>;
/// the jobs infrastructure task adds <c>job</c> and <c>processed_event</c> here as further idempotent statements.
/// </summary>
internal sealed class JobsSchemaMigrator(string connectionString) : IModuleMigrator
{
    public const string SchemaName = "jobs";

    public string Schema => SchemaName;

    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(hashtext('modelswitch.jobs.migrate'))", cancellationToken);

        bool alreadyThere;
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('jobs.outbox_event') IS NOT NULL", connection, transaction))
        {
            alreadyThere = (bool)(await exists.ExecuteScalarAsync(cancellationToken))!;
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
            CREATE INDEX IF NOT EXISTS ix_outbox_event_unpublished ON jobs.outbox_event (published_at) WHERE published_at IS NULL;
            """,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return alreadyThere ? [] : ["outbox_event"];
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
