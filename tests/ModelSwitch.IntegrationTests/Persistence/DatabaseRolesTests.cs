using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.Migrator;
using ModelSwitch.SharedKernel.Persistence;
using Npgsql;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Persistence;

/// <summary>The Migrator's least-privilege roles (data-model.md "Database roles", NFR-03), checked as the roles themselves.</summary>
public sealed class DatabaseRolesTests(PostgresFixture postgres)
{
    private const string _appPassword = "app-secret-with-'quote";

    [Fact]
    public async Task App_role_can_read_and_write_module_tables_but_not_update_model_price()
    {
        var admin = await MigratedAndGrantedAsync();
        var app = AsRole(admin, DatabaseRoles.AppRole, _appPassword);

        await ExecuteAsync(app, "insert into identity.tenant (id, name, status, created_at, updated_at) values (gen_random_uuid(), 'acme', 'active', now(), now())");
        await ExecuteAsync(app, "update identity.tenant set name = 'acme2'");
        (await ScalarAsync(app, "select count(*) from registry.model_price")).ShouldBe("0");

        var denied = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, "update registry.model_price set currency = 'USD'"));
        denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, "delete from registry.model_price")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task App_role_cannot_touch_migration_history_or_run_ddl()
    {
        var admin = await MigratedAndGrantedAsync();
        var app = AsRole(admin, DatabaseRoles.AppRole, _appPassword);

        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, "select * from identity.__ef_migrations_history")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, "create table identity.rogue (id int)")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Audit_records_are_append_only_for_the_app_role_and_regrants_are_idempotent()
    {
        var admin = await MigratedAndGrantedAsync();

        await ApplyAsync(admin); // re-running the grants is a no-op
        var app = AsRole(admin, DatabaseRoles.AppRole, _appPassword);

        await ExecuteAsync(app, "insert into identity.audit_record (id, occurred_at, action, target_type, details) values (gen_random_uuid(), now(), 'api_key.created', 'api_key', '{}')");
        (await ScalarAsync(app, "select count(*) from identity.audit_record")).ShouldBe("1");
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, "update identity.audit_record set action = 'x'")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, "delete from identity.audit_record")))
            .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Retention_role_can_delete_retention_managed_tables_only()
    {
        var admin = await MigratedAndGrantedAsync();

        (await ScalarAsync(admin, $"select has_table_privilege('{DatabaseRoles.RetentionRole}', 'identity.audit_record', 'DELETE')")).ShouldBe("True");
        (await ScalarAsync(admin, $"select has_table_privilege('{DatabaseRoles.RetentionRole}', 'identity.audit_record', 'UPDATE')")).ShouldBe("False");
        (await ScalarAsync(admin, $"select has_table_privilege('{DatabaseRoles.RetentionRole}', 'identity.tenant', 'DELETE')")).ShouldBe("False");
    }

    private async Task<string> MigratedAndGrantedAsync()
    {
        var admin = await postgres.CreateDatabaseAsync();
        await using (var services = ModuleServices.ForMigrator(admin))
        {
            foreach (var migrator in services.GetServices<IModuleMigrator>())
            {
                await migrator.MigrateAsync(CancellationToken.None);
            }
        }

        await ApplyAsync(admin);
        return admin;
    }

    private static Task ApplyAsync(string admin)
        => new DatabaseRoles(NullLogger<DatabaseRoles>.Instance)
            .ApplyAsync(admin, ["identity", "registry"], _appPassword, retentionPassword: null, CancellationToken.None);

    private static string AsRole(string admin, string role, string password)
        => new NpgsqlConnectionStringBuilder(admin) { Username = role, Password = password }.ConnectionString;

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
    }
}
