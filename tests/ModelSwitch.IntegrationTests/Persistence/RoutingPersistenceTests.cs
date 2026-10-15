using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.Routing.Contracts;
using Npgsql;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Persistence;

public sealed class RoutingPersistenceTests(PostgresFixture postgres)
{
    private const string _policyId = "0192f0c8-0000-7000-8000-0000000000a1";
    private const string _modelId = "0192f0c8-0000-7000-8000-000000000001";
    private const string _otherModelId = "0192f0c8-0000-7000-8000-000000000002";

    [Fact]
    public async Task Policy_store_returns_the_latest_version_of_the_policy_an_alias_points_to()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await SeedAsync(connectionString);
        await using var services = ModuleServices.ForRouting(connectionString);

        var snapshot = await services.GetRequiredService<IPolicyStore>().GetActiveForAliasAsync("balanced", CancellationToken.None);

        snapshot.ShouldNotBeNull();
        snapshot.PolicyId.ShouldBe(Guid.Parse(_policyId));
        snapshot.Name.ShouldBe("balanced-policy");
        snapshot.Version.ShouldBe(2);
        snapshot.Definition.Strategy.ShouldBe(PolicyStrategy.Cost);
        snapshot.Definition.Candidates.ShouldBe([Guid.Parse(_modelId), Guid.Parse(_otherModelId)]);
        snapshot.Definition.AllowedProviders.ShouldBe(["openai-main"]);
    }

    [Fact]
    public async Task Policy_store_returns_null_for_an_unknown_alias()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await SeedAsync(connectionString);
        await using var services = ModuleServices.ForRouting(connectionString);

        (await services.GetRequiredService<IPolicyStore>().GetActiveForAliasAsync("nope", CancellationToken.None)).ShouldBeNull();
    }

    [Theory]
    [InlineData("Upper")]
    [InlineData("-leading")]
    [InlineData("has space")]
    public async Task Alias_names_must_match_the_documented_pattern(string alias)
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await SeedAsync(connectionString);

        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(
            connectionString, $"insert into routing.model_alias (alias, policy_id, updated_at) values ('{alias}', '{_policyId}', now())")))
            .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Policy_versions_are_unique_per_policy_and_version_number()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await SeedAsync(connectionString);

        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(
            connectionString,
            $"insert into routing.routing_policy_version (policy_id, version, definition, resolved_candidates, created_at) values ('{_policyId}', 1, '{{}}', '{{}}', now())")))
            .SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    private static async Task SeedAsync(string connectionString)
    {
        await ExecuteAsync(connectionString, $"insert into routing.routing_policy values ('{_policyId}', 'balanced-policy', 2, now())");
        await ExecuteAsync(
            connectionString,
            $$"""
            insert into routing.routing_policy_version (policy_id, version, definition, resolved_candidates, created_at) values
              ('{{_policyId}}', 1, '{"strategy":"static","candidates":["openai-main/a"]}', '{"openai-main/a":"{{_modelId}}"}', now()),
              ('{{_policyId}}', 2, '{"strategy":"cost","candidates":["openai-main/a","openai-main/b"],"allowedProviders":["openai-main"]}',
               '{"openai-main/a":"{{_modelId}}","openai-main/b":"{{_otherModelId}}"}', now())
            """);
        await ExecuteAsync(connectionString, $"insert into routing.model_alias (alias, policy_id, description, updated_at) values ('balanced', '{_policyId}', null, now())");
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
