using ModelSwitch.SharedKernel.Persistence;
using Npgsql;

namespace ModelSwitch.Configuration.Infrastructure;

/// <summary>
/// Creates the <c>config</c> schema: one single-row table, so plain idempotent DDL rather than EF Core migrations
/// (no entity model to snapshot). Add later changes as further idempotent statements; the returned list names what was applied.
/// </summary>
internal sealed class ConfigurationMigrator(string connectionString) : IModuleMigrator
{
    public const string SchemaName = "config";

    public string Schema => SchemaName;

    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Concurrent migrators (replicas) must not race the CREATE statements.
        await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(hashtext('modelswitch.config.migrate'))", cancellationToken);

        bool alreadyThere;
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('config.config_version') IS NOT NULL", connection, transaction))
        {
            alreadyThere = (bool)(await exists.ExecuteScalarAsync(cancellationToken))!;
        }

        await ExecuteAsync(connection, transaction, "CREATE SCHEMA IF NOT EXISTS config", cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS config.config_version (
                id boolean PRIMARY KEY DEFAULT true,
                version bigint NOT NULL,
                CONSTRAINT ck_config_version_single_row CHECK (id)
            );
            INSERT INTO config.config_version (id, version) VALUES (true, 0) ON CONFLICT (id) DO NOTHING;
            """,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return alreadyThere ? [] : ["config_version"];
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
