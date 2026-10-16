using Microsoft.Extensions.Logging;
using Npgsql;

namespace ModelSwitch.Migrator;

/// <summary>
/// Creates the least-privilege runtime roles and (re)applies their grants after the module migrations
/// (data-model.md "Database roles", NFR-03). Every statement is idempotent, so the Migrator can run on every deploy.
/// </summary>
internal sealed partial class DatabaseRoles(ILogger<DatabaseRoles> logger)
{
    public const string AppRole = "modelswitch_app";
    public const string RetentionRole = "modelswitch_retention";

    // Append-only by design: the app role may INSERT and SELECT but never UPDATE or DELETE these.
    private static readonly string[] _appendOnlyTables = ["audit_record", "model_price", "routing_policy_version"];

    // Functions the worker's roles may run: SECURITY DEFINER maintenance owned by the migrator role (data-model.md "Partitioning").
    private static readonly string[] _maintenanceFunctions = ["maintain_request_partitions"];

    // Tables the worker's retention job deletes from (data-model.md "Retention").
    private static readonly string[] _retentionTables =
    [
        "inference_request", "provider_attempt", "usage_record", "usage_hourly", "audit_record",
        "outbox_event", "processed_event", "job",
    ];

    /// <summary>
    /// A role that does not exist is created only when its password is supplied (<paramref name="appPassword"/>), or, for
    /// the retention role, as <c>NOLOGIN</c> otherwise. A role that already exists, for example one a DBA created, is never
    /// altered, only granted privileges. Without the app password and without an existing app role, grants are skipped.
    /// </summary>
    public async Task ApplyAsync(
        string connectionString,
        IReadOnlyCollection<string> schemas,
        string? appPassword,
        string? retentionPassword,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Roles are cluster-wide, so concurrent migrators (several databases or replicas) must not both create one.
        await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(hashtext('modelswitch.roles'))", cancellationToken);

        var appReady = await EnsureRoleAsync(connection, transaction, AppRole, appPassword, createWithoutPassword: false, cancellationToken);
        var retentionReady = await EnsureRoleAsync(connection, transaction, RetentionRole, retentionPassword, createWithoutPassword: true, cancellationToken);

        foreach (var schema in schemas)
        {
            if (appReady)
            {
                await GrantAppAsync(connection, transaction, schema, cancellationToken);
            }

            if (retentionReady)
            {
                await GrantRetentionAsync(connection, transaction, schema, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        LogApplied(logger, schemas.Count, appReady, retentionReady);
    }

    private async Task<bool> EnsureRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string role,
        string? password,
        bool createWithoutPassword,
        CancellationToken cancellationToken)
    {
        await using (var exists = new NpgsqlCommand("SELECT 1 FROM pg_roles WHERE rolname = @name", connection, transaction))
        {
            exists.Parameters.AddWithValue("name", role);
            if (await exists.ExecuteScalarAsync(cancellationToken) is not null)
            {
                return true;
            }
        }

        if (string.IsNullOrEmpty(password) && !createWithoutPassword)
        {
            LogRoleSkipped(logger, role);
            return false;
        }

        // CREATE ROLE cannot take bind parameters, so the server quotes the identifier and the password literal.
        var login = string.IsNullOrEmpty(password) ? "NOLOGIN" : "LOGIN PASSWORD %L";
        await using var build = new NpgsqlCommand($"SELECT format('CREATE ROLE %I {login}', @name{(string.IsNullOrEmpty(password) ? string.Empty : ", @password")})", connection, transaction);
        build.Parameters.AddWithValue("name", role);
        if (!string.IsNullOrEmpty(password))
        {
            build.Parameters.AddWithValue("password", password);
        }

        var statement = (string)(await build.ExecuteScalarAsync(cancellationToken))!;

        // The advisory lock is per database, but roles are per cluster: a migrator for another database
        // on the same server can create the role between our check and our CREATE. That is success too.
        await transaction.SaveAsync("create_role", cancellationToken);
        try
        {
            await ExecuteAsync(connection, transaction, statement, cancellationToken);
        }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.DuplicateObject or PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync("create_role", cancellationToken);
            return true;
        }

        LogRoleCreated(logger, role);
        return true;
    }

    private static async Task GrantAppAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, CancellationToken cancellationToken)
    {
        // Per table rather than ALL TABLES, so the EF migrations history stays out of the app role's reach.
        var sql = $"""
            DO $do$
            DECLARE t record;
            BEGIN
                EXECUTE format('GRANT USAGE ON SCHEMA %I TO {AppRole}', @schema);
                EXECUTE format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA %I TO {AppRole}', @schema);
                FOR t IN SELECT tablename FROM pg_tables WHERE schemaname = @schema AND tablename <> '__ef_migrations_history' LOOP
                    EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON %I.%I TO {AppRole}', @schema, t.tablename);
                    IF t.tablename = ANY (ARRAY[{Literals(_appendOnlyTables)}]) THEN
                        EXECUTE format('REVOKE UPDATE, DELETE ON %I.%I FROM {AppRole}', @schema, t.tablename);
                    END IF;
                END LOOP;
            END
            $do$
            """;
        await ExecuteForSchemaAsync(connection, transaction, sql, schema, cancellationToken);
        await GrantFunctionsAsync(connection, transaction, schema, AppRole, cancellationToken);
    }

    private static async Task GrantFunctionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string role, CancellationToken cancellationToken)
    {
        var sql = $"""
            DO $do$
            DECLARE f record;
            BEGIN
                FOR f IN SELECT p.oid::regprocedure AS signature FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                         WHERE n.nspname = @schema AND p.proname = ANY (ARRAY[{Literals(_maintenanceFunctions)}]) LOOP
                    EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO {role}', f.signature);
                END LOOP;
            END
            $do$
            """;
        await ExecuteForSchemaAsync(connection, transaction, sql, schema, cancellationToken);
    }

    private static async Task GrantRetentionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, CancellationToken cancellationToken)
    {
        var sql = $"""
            DO $do$
            DECLARE t record;
            BEGIN
                EXECUTE format('GRANT USAGE ON SCHEMA %I TO {RetentionRole}', @schema);
                FOR t IN SELECT tablename FROM pg_tables WHERE schemaname = @schema AND tablename = ANY (ARRAY[{Literals(_retentionTables)}]) LOOP
                    EXECUTE format('GRANT SELECT, DELETE ON %I.%I TO {RetentionRole}', @schema, t.tablename);
                END LOOP;
            END
            $do$
            """;
        await ExecuteForSchemaAsync(connection, transaction, sql, schema, cancellationToken);
        await GrantFunctionsAsync(connection, transaction, schema, RetentionRole, cancellationToken);
    }

    private static string Literals(string[] names) => string.Join(", ", names.Select(n => $"'{n}'"));

    // DO blocks cannot take bind parameters, so the schema name is quoted by the server and inlined as a literal.
    private static async Task ExecuteForSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string doBlock, string schema, CancellationToken cancellationToken)
    {
        await using var quote = new NpgsqlCommand("SELECT quote_literal(@schema)", connection, transaction);
        quote.Parameters.AddWithValue("schema", schema);
        var literal = (string)(await quote.ExecuteScalarAsync(cancellationToken))!;
        await ExecuteAsync(connection, transaction, doBlock.Replace("@schema", literal, StringComparison.Ordinal), cancellationToken);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database roles: grants applied to {Schemas} schema(s) (app role: {App}, retention role: {Retention}).")]
    private static partial void LogApplied(ILogger logger, int schemas, bool app, bool retention);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created database role {Role}.")]
    private static partial void LogRoleCreated(ILogger logger, string role);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database role {Role} does not exist and no password was supplied (POSTGRES_APP_PASSWORD), so it was not created and its grants were skipped.")]
    private static partial void LogRoleSkipped(ILogger logger, string role);
}
