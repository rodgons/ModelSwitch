using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.Migrator;
using ModelSwitch.SharedKernel.Persistence;
using ModelSwitch.Usage.Infrastructure;
using Npgsql;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Persistence;

/// <summary>The partitioned <c>usage</c> tables (data-model.md "usage" and "Partitioning").</summary>
public sealed class UsageSchemaTests(PostgresFixture postgres)
{
    // Roles are cluster-wide, so every test class sharing the container must use the password the role was created with.
    private const string _appPassword = "app-secret-with-'quote";
    private const string _tenant = "0192f0c8-0000-7000-8000-000000000001";
    private const string _requestColumns = "(id, started_at, tenant_id, origin, api_key_id, evaluation_run_id, requested_alias, streaming, input_bytes, status, usage_status, trace_id, attempt_count, had_fallback)";

    [Fact]
    public async Task Applied_schema_matches_the_ef_model_with_no_pending_changes()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();

        await using var usage = new UsageDbContext(new DbContextOptionsBuilder<UsageDbContext>().UseNpgsql(connectionString).Options);

        usage.Database.HasPendingModelChanges().ShouldBeFalse("a model change needs a new Usage migration");
    }

    [Fact]
    public async Task Request_and_attempt_tables_are_range_partitioned_by_month_on_started_at()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();

        var parents = await QueryAsync(
            connectionString,
            """
            select c.relname || ':' || pg_get_partkeydef(c.oid)
            from pg_class c join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = 'usage' and c.relkind = 'p' order by 1
            """);

        parents.ShouldBe(["inference_request:RANGE (started_at)", "provider_attempt:RANGE (started_at)"]);
    }

    [Fact]
    public async Task Partitions_exist_for_the_previous_current_and_the_next_twelve_months()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();

        (await PartitionCountAsync(connectionString, "inference_request")).ShouldBe("14");
        (await PartitionCountAsync(connectionString, "provider_attempt")).ShouldBe("14");

        var routedTo = await QueryAsync(connectionString, InsertRequest("api", "gen_random_uuid()", "null") + " returning tableoid::regclass::text");
        routedTo.Single().ShouldStartWith("usage.inference_request_y");
    }

    [Fact]
    public async Task Ensuring_partitions_is_idempotent_and_extends_the_window()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();

        await ExecuteAsync(connectionString, "select usage.ensure_request_partitions(1, 12)");
        (await PartitionCountAsync(connectionString, "provider_attempt")).ShouldBe("14");

        await ExecuteAsync(connectionString, "select usage.ensure_request_partitions(1, 15)");
        (await PartitionCountAsync(connectionString, "provider_attempt")).ShouldBe("17");
    }

    [Fact]
    public async Task The_migrator_restores_the_partition_window_on_a_run_with_nothing_pending()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await ExecuteAsync(connectionString, "drop table usage.inference_request_y" + FarMonthSuffix());
        (await PartitionCountAsync(connectionString, "inference_request")).ShouldBe("13");
        await using var services = ModuleServices.ForMigrator(connectionString);

        foreach (var migrator in services.GetServices<IModuleMigrator>())
        {
            await migrator.MigrateAsync(CancellationToken.None);
        }

        (await PartitionCountAsync(connectionString, "inference_request")).ShouldBe("14");
    }

    [Fact]
    public async Task Origin_ids_check_api_requests_carry_a_key_and_evaluation_requests_a_run()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();

        await ExecuteAsync(connectionString, InsertRequest("api", "gen_random_uuid()", "null"));
        await ExecuteAsync(connectionString, InsertRequest("evaluation", "null", "gen_random_uuid()"));
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connectionString, InsertRequest("api", "null", "null"))))
            .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connectionString, InsertRequest("evaluation", "gen_random_uuid()", "gen_random_uuid()"))))
            .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task App_role_can_write_the_partitioned_tables_and_retention_role_can_delete_from_them()
    {
        var admin = await postgres.CreateMigratedDatabaseAsync();
        await new DatabaseRoles(NullLogger<DatabaseRoles>.Instance)
            .ApplyAsync(admin, ["usage"], appPassword: _appPassword, retentionPassword: null, CancellationToken.None);
        var app = new NpgsqlConnectionStringBuilder(admin) { Username = DatabaseRoles.AppRole, Password = _appPassword }.ConnectionString;

        await ExecuteAsync(app, InsertRequest("api", "gen_random_uuid()", "null"));
        await ExecuteAsync(app, "update usage.inference_request set status = 'failed'");

        (await QueryAsync(admin, $"select has_table_privilege('{DatabaseRoles.RetentionRole}', 'usage.inference_request', 'DELETE')::text")).ShouldBe(["true"]);
        (await QueryAsync(admin, $"select has_table_privilege('{DatabaseRoles.RetentionRole}', 'usage.provider_attempt', 'DELETE')::text")).ShouldBe(["true"]);
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, "select * from usage.__ef_migrations_history")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    private static string InsertRequest(string origin, string apiKey, string run)
        => $"insert into usage.inference_request {_requestColumns} values (gen_random_uuid(), now(), '{_tenant}', '{origin}', {apiKey}, {run}, 'a', false, 1, 'running', 'unavailable', '', 0, false)";

    private static async Task<string> PartitionCountAsync(string connectionString, string parent)
        => (await QueryAsync(
            connectionString,
            $"select count(*)::text from pg_inherits i join pg_class p on p.oid = i.inhparent join pg_namespace n on n.oid = p.relnamespace where n.nspname = 'usage' and p.relname = '{parent}'")).Single();

    // The last month of the migrated window (twelve months ahead), whatever today's date is.
    private static string FarMonthSuffix()
    {
        var today = TimeProvider.System.GetUtcNow();
        var month = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(12);
        return $"{month:yyyy}m{month:MM}";
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> QueryAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        }

        return rows;
    }
}
