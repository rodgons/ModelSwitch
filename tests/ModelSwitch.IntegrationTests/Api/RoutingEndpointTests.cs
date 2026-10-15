using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelSwitch.IntegrationTests.Infrastructure;
using Npgsql;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>Routing through the real host: aliases and Policy Versions come from the configuration snapshot (architecture.md section 6).</summary>
public sealed class RoutingEndpointTests(GatewayFixture gateway) : IClassFixture<GatewayFixture>
{
    private static StringContent Body(string alias)
        => new(
            new JsonObject
            {
                ["model"] = alias,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }),
            }.ToJsonString(),
            Encoding.UTF8,
            "application/json");

    private static HttpRequestMessage Post(string alias, string header, string value)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = Body(alias) };
        message.Headers.Add(header, value);
        return message;
    }

    [Fact]
    public async Task The_policy_version_and_routing_decision_are_recorded_on_the_request()
    {
        var response = await gateway.Client.PostAsync("/v1/chat/completions", Body(GatewayFixture.ChatAlias), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var requestId = response.Headers.GetValues("X-Request-Id").Single();
        var row = await Row($"select policy_id is not null, policy_version, routing_decision->>'strategy', jsonb_array_length(routing_decision->'ordered') from usage.inference_request where id = '{requestId}'");
        row.ShouldBe(["True", "1", "static", "1"]);
    }

    [Fact]
    public async Task A_new_policy_version_is_used_by_the_next_request_without_a_restart()
    {
        // A second alias whose policy is saved as version 1 and then version 2: the alias always resolves to the latest.
        var models = await Row("select id::text from registry.provider_model where provider_model_id = 'mock-echo' and provider_id = (select id from registry.provider where name = 'mock-openai')");
        var modelId = Guid.Parse(models[0]);
        var policyId = Guid.CreateVersion7();
        await gateway.WriteConfigurationAsync(
            $$"""
            insert into routing.routing_policy values ('{{policyId}}', 'versioned-policy', 1, now());
            insert into routing.routing_policy_version (policy_id, version, definition, resolved_candidates, created_at)
              values ('{{policyId}}', 1, '{"strategy":"static","candidates":["mock-openai/mock-echo"]}', '{"mock-openai/mock-echo":"{{modelId}}"}', now());
            insert into routing.model_alias (alias, policy_id, description, updated_at) values ('versioned', '{{policyId}}', null, now());
            """);

        var first = await gateway.Client.PostAsync("/v1/chat/completions", Body("versioned"), TestContext.Current.CancellationToken);
        await gateway.WriteConfigurationAsync(
            $$"""
            insert into routing.routing_policy_version (policy_id, version, definition, resolved_candidates, created_at)
              values ('{{policyId}}', 2, '{"strategy":"static","candidates":["mock-openai/mock-echo"]}', '{"mock-openai/mock-echo":"{{modelId}}"}', now());
            update routing.routing_policy set latest_version = 2 where id = '{{policyId}}';
            """);
        var second = await gateway.Client.PostAsync("/v1/chat/completions", Body("versioned"), TestContext.Current.CancellationToken);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Row($"select policy_version::text from usage.inference_request where id = '{first.Headers.GetValues("X-Request-Id").Single()}'")).ShouldBe(["1"]);
        (await Row($"select policy_version::text from usage.inference_request where id = '{second.Headers.GetValues("X-Request-Id").Single()}'")).ShouldBe(["2"]);
        var list = await (await gateway.Client.GetAsync("/v1/models", TestContext.Current.CancellationToken)).Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        list.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()).ShouldContain("versioned");
    }

    [Fact]
    public async Task A_model_without_a_price_is_not_eligible_so_the_alias_answers_422_no_eligible_model()
    {
        var providerId = (await Row("select id::text from registry.provider where name = 'mock-openai'"))[0];
        var modelId = Guid.CreateVersion7();
        await gateway.WriteConfigurationAsync(
            $$"""
            insert into registry.provider_model (id, provider_id, provider_model_id, display_name, capabilities, enabled, created_at, updated_at)
              values ('{{modelId}}', '{{providerId}}', 'mock-unpriced', 'x', '{"streaming":true,"contextWindow":8000,"maxOutputTokens":4000}', true, now(), now());
            {{GatewayFixture.PolicyAndAliasSql("unpriced", [("mock-openai", "mock-unpriced", modelId)])}}
            """);

        var response = await gateway.Client.PostAsync("/v1/chat/completions", Body("unpriced"), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("error").GetProperty("code").GetString().ShouldBe("no_eligible_model");
        problem.ToString().ShouldNotContain("mock-unpriced", Case.Sensitive, "model names are never shown");
        var requestId = response.Headers.GetValues("X-Request-Id").Single();
        (await Row($"select error_code, policy_version::text, routing_decision::text like '%no_price%' from usage.inference_request where id = '{requestId}'"))
            .ShouldBe(["no_eligible_model", "1", "True"]);
    }

    [Fact]
    public async Task A_timeout_header_shortens_the_deadline()
    {
        await gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "latency=3000");
        try
        {
            var watch = Stopwatch.StartNew();
            using var request = Post(GatewayFixture.ChatAlias, "X-ModelSwitch-Timeout-Ms", "200");

            var response = await gateway.Client.SendAsync(request, TestContext.Current.CancellationToken);

            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2.5));
            response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            problem.GetProperty("error").GetProperty("code").GetString().ShouldBe("deadline_exceeded");
        }
        finally
        {
            await gateway.SetFaultAsync(null, null);
        }
    }

    [Theory]
    [InlineData("X-ModelSwitch-Timeout-Ms", "0")]
    [InlineData("X-ModelSwitch-Timeout-Ms", "soon")]
    [InlineData("X-ModelSwitch-Task", "Not Valid")]
    public async Task An_invalid_modelswitch_header_is_a_400_invalid_request_naming_the_header(string header, string value)
    {
        using var request = Post(GatewayFixture.ChatAlias, header, value);

        var response = await gateway.Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("error").GetProperty("code").GetString().ShouldBe("invalid_request");
        problem.GetProperty("errors").TryGetProperty(header, out _).ShouldBeTrue();
    }

    private async Task<string[]> Row(string sql)
    {
        await using var connection = new NpgsqlConnection(gateway.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue("expected a row");
        return [.. Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!)];
    }
}
