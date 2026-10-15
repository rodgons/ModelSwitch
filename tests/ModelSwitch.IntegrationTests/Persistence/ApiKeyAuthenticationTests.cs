using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Persistence;
using Npgsql;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Persistence;

/// <summary>The seeded key authenticates through the real database, and rows change the outcome (testing.md section 2, Auth).</summary>
public sealed class ApiKeyAuthenticationTests(PostgresFixture postgres)
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Seeded_key_authenticates_and_is_stored_only_as_an_hmac()
    {
        var (connectionString, seeded) = await SeedAsync();
        await using var api = ModuleServices.ForApi(connectionString, _time);

        var result = await api.GetRequiredService<IApiKeyAuthenticator>().AuthenticateAsync(seeded.PlaintextKey, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TenantId.ShouldBe(seeded.TenantId);
        result.Value.ApiKeyId.ShouldBe(seeded.ApiKeyId);
        result.Value.Scopes.ShouldBe(ApiKeyScopes.All);

        var stored = await ScalarAsync(connectionString, "select encode(key_hash, 'hex') from identity.api_key");
        stored.Length.ShouldBe(64);
        seeded.PlaintextKey.ShouldNotContain(stored);
        (await ScalarAsync(connectionString, "select count(*) from identity.api_key where name like 'msk_%'")).ShouldBe("0");
    }

    [Fact]
    public async Task Altered_key_is_invalid()
    {
        var (connectionString, seeded) = await SeedAsync();
        await using var api = ModuleServices.ForApi(connectionString, _time);
        var forged = seeded.PlaintextKey[..^1] + (seeded.PlaintextKey[^1] == 'A' ? 'B' : 'A');

        var result = await api.GetRequiredService<IApiKeyAuthenticator>().AuthenticateAsync(forged, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.InvalidApiKey);
    }

    [Fact]
    public async Task Revoked_key_is_invalid_once_the_cache_has_expired()
    {
        var (connectionString, seeded) = await SeedAsync();
        await using var api = ModuleServices.ForApi(connectionString, _time);
        var authenticator = api.GetRequiredService<IApiKeyAuthenticator>();
        (await authenticator.AuthenticateAsync(seeded.PlaintextKey, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        await ExecuteAsync(connectionString, "update identity.api_key set revoked_at = timestamptz '2026-10-15 11:59:00+00'");

        (await authenticator.AuthenticateAsync(seeded.PlaintextKey, CancellationToken.None)).IsSuccess.ShouldBeTrue("served from the 30 s cache");
        _time.Advance(TimeSpan.FromSeconds(30));
        (await authenticator.AuthenticateAsync(seeded.PlaintextKey, CancellationToken.None)).Error.Code.ShouldBe(ErrorCode.InvalidApiKey);
    }

    [Fact]
    public async Task Expired_key_is_invalid()
    {
        var (connectionString, seeded) = await SeedAsync();
        await ExecuteAsync(connectionString, "update identity.api_key set expires_at = timestamptz '2026-10-15 11:00:00+00'");
        await using var api = ModuleServices.ForApi(connectionString, _time);

        var result = await api.GetRequiredService<IApiKeyAuthenticator>().AuthenticateAsync(seeded.PlaintextKey, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.InvalidApiKey);
    }

    [Fact]
    public async Task Suspended_tenant_is_tenant_suspended()
    {
        var (connectionString, seeded) = await SeedAsync();
        await ExecuteAsync(connectionString, "update identity.tenant set status = 'suspended'");
        await using var api = ModuleServices.ForApi(connectionString, _time);

        var result = await api.GetRequiredService<IApiKeyAuthenticator>().AuthenticateAsync(seeded.PlaintextKey, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.TenantSuspended);
    }

    [Fact]
    public async Task Seeding_twice_reuses_the_tenant_and_creates_a_second_distinct_key()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var services = ModuleServices.ForMigrator(connectionString, _time);
        foreach (var migrator in services.GetServices<IModuleMigrator>())
        {
            await migrator.MigrateAsync(CancellationToken.None);
        }

        var seeder = services.GetRequiredService<IIdentitySeeder>();
        var first = await seeder.SeedAsync("acme", "first", CancellationToken.None);
        var second = await seeder.SeedAsync("acme", "second", CancellationToken.None);

        first.TenantCreated.ShouldBeTrue();
        second.TenantCreated.ShouldBeFalse();
        second.TenantId.ShouldBe(first.TenantId);
        second.PlaintextKey.ShouldNotBe(first.PlaintextKey);
        (await ScalarAsync(connectionString, "select count(*) from identity.tenant")).ShouldBe("1");
        (await ScalarAsync(connectionString, "select count(*) from identity.api_key")).ShouldBe("2");
    }

    private async Task<(string ConnectionString, SeededApiKey Seeded)> SeedAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var services = ModuleServices.ForMigrator(connectionString, _time);
        foreach (var migrator in services.GetServices<IModuleMigrator>())
        {
            await migrator.MigrateAsync(CancellationToken.None);
        }

        var seeded = await services.GetRequiredService<IIdentitySeeder>().SeedAsync("acme", "test", CancellationToken.None);
        return (connectionString, seeded);
    }

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
