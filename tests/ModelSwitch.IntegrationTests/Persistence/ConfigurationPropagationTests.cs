using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.Api;
using ModelSwitch.Configuration.Contracts;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.SharedKernel.Configuration;
using Npgsql;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Persistence;

/// <summary>Runtime configuration propagation (architecture.md section 6, testing.md section 2 "Config propagation").</summary>
public sealed class ConfigurationPropagationTests(PostgresFixture postgres)
{
    private const string _providerId = "0192f0c8-0000-7000-8000-0000000000b1";
    private const string _modelId = "0192f0c8-0000-7000-8000-0000000000b2";
    private const string _policyId = "0192f0c8-0000-7000-8000-0000000000b3";

    [Fact]
    public async Task A_config_write_on_one_host_reaches_the_other_host_within_one_second_via_notify()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var hostA = new ApiFactory(connectionString);
        await using var hostB = new ApiFactory(connectionString);
        var snapshotB = hostB.Services.GetRequiredService<IConfigurationSnapshotProvider>();
        _ = hostA.Services;
        snapshotB.Current.Version.ShouldBe(0);
        snapshotB.Current.Routing.AliasToPolicy.ShouldBeEmpty();

        // What an admin write does: change configuration and bump the version in one transaction.
        var version = await WriteConfigurationAsync(
            connectionString,
            $$"""
            insert into registry.provider values ('{{_providerId}}', 'openai-main', 'openai_compatible', 'https://api.example.test/v1', null, 30000, true, now(), now());
            insert into registry.provider_model values ('{{_modelId}}', '{{_providerId}}', 'gpt-a', 'GPT A', '{"streaming":true,"contextWindow":8000,"maxOutputTokens":1000}', true, now(), now());
            insert into registry.model_price values (gen_random_uuid(), '{{_modelId}}', 1, null, 2, 'USD', '2026-01-01', now(), null);
            insert into routing.routing_policy values ('{{_policyId}}', 'fast-policy', 1, now());
            insert into routing.routing_policy_version (policy_id, version, definition, resolved_candidates, created_at)
              values ('{{_policyId}}', 1, '{"strategy":"static","candidates":["openai-main/gpt-a"]}', '{"openai-main/gpt-a":"{{_modelId}}"}', now());
            insert into routing.model_alias (alias, policy_id, description, updated_at) values ('fast', '{{_policyId}}', null, now());
            """);

        await WaitForVersionAsync(snapshotB, version, TimeSpan.FromSeconds(1));
        var routing = snapshotB.Current.Routing;
        routing.AliasToPolicy["fast"].ShouldBe(Guid.Parse(_policyId));
        routing.Policies[Guid.Parse(_policyId)].Version.ShouldBe(1);
        var model = routing.Models[Guid.Parse(_modelId)];
        model.ProviderName.ShouldBe("openai-main");
        model.Capabilities.Streaming.ShouldBeTrue();
        model.Capabilities.ContextWindow.ShouldBe(8000);
        model.Price.ShouldNotBeNull().InputPerMillion.ShouldBe(1m);

        // The same for the writing host's own snapshot, and a second write moves both to the next Policy Version.
        var snapshotA = hostA.Services.GetRequiredService<IConfigurationSnapshotProvider>();
        await WaitForVersionAsync(snapshotA, version, TimeSpan.FromSeconds(1));
        var inFlight = snapshotB.Current;

        var next = await WriteConfigurationAsync(
            connectionString,
            $$"""
            insert into routing.routing_policy_version (policy_id, version, definition, resolved_candidates, created_at)
              values ('{{_policyId}}', 2, '{"strategy":"static","candidates":["openai-main/gpt-a"]}', '{"openai-main/gpt-a":"{{_modelId}}"}', now());
            update routing.routing_policy set latest_version = 2 where id = '{{_policyId}}';
            """);

        await WaitForVersionAsync(snapshotB, next, TimeSpan.FromSeconds(1));
        snapshotB.Current.Routing.Policies[Guid.Parse(_policyId)].Version.ShouldBe(2);
        inFlight.Routing.Policies[Guid.Parse(_policyId)].Version.ShouldBe(1); // a request that started earlier keeps its snapshot
    }

    [Fact]
    public async Task A_rolled_back_config_write_neither_bumps_the_version_nor_changes_the_snapshot()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var host = new ApiFactory(connectionString);
        var snapshots = host.Services.GetRequiredService<IConfigurationSnapshotProvider>();

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
            (await ConfigVersion.BumpAsync(connection, transaction, TestContext.Current.CancellationToken)).ShouldBe(1);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        await Task.Delay(300, TestContext.Current.CancellationToken);
        snapshots.Current.Version.ShouldBe(0);
        await using var check = new NpgsqlConnection(connectionString);
        await check.OpenAsync(TestContext.Current.CancellationToken);
        (await ConfigVersion.ReadAsync(check, TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Ready_reports_the_loaded_configuration_snapshot()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await using var host = new ApiFactory(connectionString);
        using var client = host.CreateClient();

        var response = await client.GetAsync("/ready", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("checks").GetProperty("configuration").GetString().ShouldBe("ok");
    }

    [Fact]
    public async Task Ready_is_not_ready_while_the_snapshot_cannot_be_loaded()
    {
        // The database exists but was never migrated, so config.config_version is missing and the first load fails.
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var host = new ApiFactory(connectionString);
        using var client = host.CreateClient();

        var response = await client.GetAsync("/ready", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("checks").GetProperty("configuration").GetString().ShouldBe("unavailable");
        host.Services.GetRequiredService<IConfigurationSnapshotProvider>().IsLoaded.ShouldBeFalse();
    }

    private static async Task<long> WriteConfigurationAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var version = await ConfigVersion.BumpAsync(connection, transaction, TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return version;
    }

    private static async Task WaitForVersionAsync(IConfigurationSnapshotProvider snapshots, long version, TimeSpan within)
    {
        var deadline = Environment.TickCount64 + (long)within.TotalMilliseconds;
        while (snapshots.Current.Version < version)
        {
            Environment.TickCount64.ShouldBeLessThan(deadline, $"the snapshot did not reach version {version} within {within}");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class ApiFactory(string connectionString) : WebApplicationFactory<ApiHost>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString,
                ["Security:ApiKeyPepper"] = ModuleServices.Pepper,
            }));
    }
}
