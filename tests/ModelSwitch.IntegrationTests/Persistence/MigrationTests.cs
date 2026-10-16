using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.Identity.Infrastructure;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.Registry.Infrastructure;
using ModelSwitch.Routing.Infrastructure;
using ModelSwitch.SharedKernel.Persistence;
using Npgsql;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Persistence;

public sealed class MigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Migrations_apply_from_empty_then_a_second_run_applies_nothing()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var services = ModuleServices.ForMigrator(connectionString);

        var first = await ApplyAllAsync(services);
        var second = await ApplyAllAsync(services);

        first["identity"].Count.ShouldBe(3);
        first["identity"][0].ShouldEndWith("_Initial");
        first["identity"][1].ShouldEndWith("_Administrators");
        first["identity"][2].ShouldEndWith("_TenantLimits");
        first["registry"].Count.ShouldBe(3);
        first["registry"][0].ShouldEndWith("_Initial");
        first["registry"][1].ShouldEndWith("_ProviderHealthSnapshot");
        first["registry"][2].ShouldEndWith("_ProviderSecret");
        first["routing"].ShouldHaveSingleItem().ShouldEndWith("_Initial");
        first["config"].ShouldBe(["config_version"]);
        second["identity"].ShouldBeEmpty();
        second["registry"].ShouldBeEmpty();
        second["routing"].ShouldBeEmpty();
        first["usage"].Count.ShouldBe(2);
        first["usage"][0].ShouldEndWith("_Initial");
        first["usage"][1].ShouldEndWith("_Budgets");
        first["jobs"].ShouldBe(["outbox_event"]);
        second["usage"].ShouldBeEmpty();
        second["config"].ShouldBeEmpty();
        second["jobs"].ShouldBeEmpty();
    }

    [Fact]
    public async Task Migrations_apply_in_the_fixed_order_identity_registry_routing_usage_config_jobs()
    {
        await using var services = ModuleServices.ForMigrator(await postgres.CreateDatabaseAsync());

        services.GetServices<IModuleMigrator>().Select(m => m.Schema).ShouldBe(["identity", "registry", "routing", "usage", "config", "jobs"]);
    }

    [Fact]
    public async Task Applied_schema_matches_the_ef_model_with_no_pending_changes()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var services = ModuleServices.ForMigrator(connectionString);
        await ApplyAllAsync(services);

        await using var identity = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(connectionString).Options);
        await using var registry = new RegistryDbContext(new DbContextOptionsBuilder<RegistryDbContext>().UseNpgsql(connectionString).Options);
        await using var routing = new RoutingDbContext(new DbContextOptionsBuilder<RoutingDbContext>().UseNpgsql(connectionString).Options);

        identity.Database.HasPendingModelChanges().ShouldBeFalse("a model change needs a new Identity migration");
        registry.Database.HasPendingModelChanges().ShouldBeFalse("a model change needs a new Registry migration");
        routing.Database.HasPendingModelChanges().ShouldBeFalse("a model change needs a new Routing migration");
    }

    [Fact]
    public async Task Tables_are_created_in_their_modules_schemas()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var services = ModuleServices.ForMigrator(connectionString);
        await ApplyAllAsync(services);

        var tables = await QueryAsync<string>(
            connectionString,
            "select table_schema || '.' || table_name from information_schema.tables where table_schema in ('config','identity','registry','routing') order by 1");

        tables.ShouldBe(
        [
            "config.config_version",
            "identity.__ef_migrations_history",
            "identity.administrator",
            "identity.administrator_session",
            "identity.administrator_tenant_grant",
            "identity.api_key",
            "identity.audit_record",
            "identity.data_protection_key",
            "identity.tenant",
            "identity.tenant_limits",
            "registry.__ef_migrations_history",
            "registry.model_price",
            "registry.provider",
            "registry.provider_health_snapshot",
            "registry.provider_model",
            "registry.provider_secret",
            "routing.__ef_migrations_history",
            "routing.model_alias",
            "routing.routing_policy",
            "routing.routing_policy_version",
        ]);
    }

    [Fact]
    public async Task Api_key_columns_follow_the_data_model()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var services = ModuleServices.ForMigrator(connectionString);
        await ApplyAllAsync(services);

        var columns = await QueryAsync<string>(
            connectionString,
            "select column_name || ':' || data_type || ':' || is_nullable from information_schema.columns where table_schema = 'identity' and table_name = 'api_key' order by ordinal_position");

        columns.ShouldBe(
        [
            "id:uuid:NO",
            "tenant_id:uuid:NO",
            "name:text:NO",
            "key_prefix:character:NO",
            "key_hash:bytea:NO",
            "scopes:ARRAY:NO",
            "expires_at:timestamp with time zone:YES",
            "revoked_at:timestamp with time zone:YES",
            "last_used_at:timestamp with time zone:YES",
            "created_at:timestamp with time zone:NO",
            "created_by_admin_id:uuid:YES",
        ]);
    }

    [Fact]
    public async Task Key_prefix_is_unique_and_key_hash_must_be_32_bytes()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var services = ModuleServices.ForMigrator(connectionString);
        await ApplyAllAsync(services);
        const string tenant = "insert into identity.tenant values ('0192f0c8-0000-7000-8000-000000000001', 't', 'active', now(), now())";
        await ExecuteAsync(connectionString, tenant);

        var oneKey = (string prefix, string hex) =>
            $"insert into identity.api_key (id, tenant_id, name, key_prefix, key_hash, scopes, created_at) values (gen_random_uuid(), '0192f0c8-0000-7000-8000-000000000001', 'k', '{prefix}', '\\x{hex}', '{{inference}}', now())";
        var hash = new string('a', 64);

        await ExecuteAsync(connectionString, oneKey("aaaaaaaa", hash));
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connectionString, oneKey("aaaaaaaa", hash))))
            .SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connectionString, oneKey("bbbbbbbb", "abcd"))))
            .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Model_price_is_immutable_history_unique_per_provider_model_and_effective_time()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var services = ModuleServices.ForMigrator(connectionString);
        await ApplyAllAsync(services);
        await ExecuteAsync(connectionString, "insert into registry.provider values ('0192f0c8-0000-7000-8000-000000000010', 'p', 'ollama', 'http://ollama:11434', null, 30000, true, now(), now())");
        await ExecuteAsync(connectionString, "insert into registry.provider_model values ('0192f0c8-0000-7000-8000-000000000020', '0192f0c8-0000-7000-8000-000000000010', 'llama', 'Llama', '{}', true, now(), now())");
        const string price = "insert into registry.model_price values (gen_random_uuid(), '0192f0c8-0000-7000-8000-000000000020', 1, null, 2, 'USD', '2026-01-01', now(), null)";

        await ExecuteAsync(connectionString, price);

        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connectionString, price)))
            .SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    private static async Task<Dictionary<string, IReadOnlyList<string>>> ApplyAllAsync(ServiceProvider services)
    {
        var applied = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var migrator in services.GetServices<IModuleMigrator>())
        {
            applied[migrator.Schema] = await migrator.MigrateAsync(CancellationToken.None);
        }

        return applied;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<T>> QueryAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetFieldValue<T>(0));
        }

        return rows;
    }
}
