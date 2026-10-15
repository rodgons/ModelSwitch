using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.SharedKernel.Http;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

public sealed class ModelsAndHealthEndpointTests(GatewayFixture gateway) : IClassFixture<GatewayFixture>
{
    [Fact]
    [HappyPath("GET /v1/models")]
    public async Task Models_lists_aliases_in_openai_format_without_provider_model_ids()
    {
        var response = await gateway.Client.GetAsync("/v1/models", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains("X-Request-Id").ShouldBeTrue();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("object").GetString().ShouldBe("list");
        var models = body.GetProperty("data").EnumerateArray().ToList();
        models.Select(m => m.GetProperty("id").GetString()).ShouldBe([GatewayFixture.ChatAlias, GatewayFixture.OllamaAlias, GatewayFixture.ResilientAlias]);
        models.ShouldAllBe(m => m.GetProperty("object").GetString() == "model" && m.GetProperty("owned_by").GetString() == "modelswitch");
        body.ToString().ShouldNotContain("mock-echo");
    }

    [Fact]
    public async Task Models_is_available_with_only_the_models_read_scope()
    {
        var response = await gateway.ModelsOnly.GetAsync("/v1/models", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Models_without_a_key_is_a_401_problem()
    {
        var response = await gateway.Anonymous.GetAsync("/v1/models", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    [HappyPath("GET /health")]
    public async Task Health_is_200_without_authentication()
    {
        var response = await gateway.Anonymous.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains("X-Request-Id").ShouldBeTrue();
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("status").GetString().ShouldBe("ok");
    }

    [Fact]
    [HappyPath("GET /ready")]
    public async Task Ready_is_200_when_postgres_is_reachable()
    {
        var response = await gateway.Anonymous.GetAsync("/ready", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("status").GetString().ShouldBe("ready");
        body.GetProperty("checks").GetProperty("postgres").GetString().ShouldBe("ok");
    }

    [Fact]
    public async Task Postgres_readiness_check_fails_when_the_database_is_unreachable()
    {
        await using var api = ModuleServices.ForApi("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1;Pooling=false");
        var check = api.GetServices<IReadinessCheck>().Single();

        (await check.CheckAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
    }
}
