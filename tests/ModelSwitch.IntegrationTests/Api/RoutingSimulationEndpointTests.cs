using System.Net;
using System.Text.Json;
using ModelSwitch.IntegrationTests.Infrastructure;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary><c>POST /admin/routing-policies/{policyId}/simulate</c> (api.md section 3, "Routing") through the real host and its snapshot.</summary>
public sealed class RoutingSimulationEndpointTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string _capabilities =
        """{"streaming":true,"jsonObject":true,"jsonSchema":false,"contextWindow":128000,"maxOutputTokens":16384,"tokenizer":"o200k_base","parameters":["temperature"]}""";

    private static readonly string[] _jsonSchema = ["jsonSchema"];

    private static string Unique(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}"[..30];

    private static async Task<JsonElement> CreatedAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return await AdminBrowser.JsonAsync(response);
    }

    /// <summary>A Provider with a cheap and a pricey priced model and an unpriced one, and a cost policy over all three.</summary>
    private async Task<(Guid PolicyId, Guid Cheap, Guid Pricey, Guid Unpriced)> PolicyAsync(AdminBrowser owner)
    {
        var provider = Unique("sim");
        var providerId = (await CreatedAsync(await owner.SendJsonAsync(HttpMethod.Post, "/admin/providers", new { name = provider, adapterType = "openai_compatible", endpoint = fixture.OpenAiEndpoint })))
            .GetProperty("id").GetGuid();
        async Task<Guid> ModelAsync(string identifier, decimal? price)
        {
            var id = (await CreatedAsync(await owner.SendJsonAsync(HttpMethod.Post, $"/admin/providers/{providerId}/models", new { providerModelId = identifier, capabilities = JsonDocument.Parse(_capabilities).RootElement })))
                .GetProperty("id").GetGuid();
            if (price is { } p)
            {
                await CreatedAsync(await owner.SendJsonAsync(HttpMethod.Post, $"/admin/models/{id}/prices", new { inputPerMillion = p, outputPerMillion = p * 2 }));
            }

            return id;
        }

        var cheap = await ModelAsync("cheap", 1m);
        var pricey = await ModelAsync("pricey", 10m);
        var unpriced = await ModelAsync("unpriced", null);
        var policy = await CreatedAsync(await owner.SendJsonAsync(
            HttpMethod.Post,
            "/admin/routing-policies",
            new { name = Unique("simpol"), definition = new { strategy = "cost", candidates = new[] { $"{provider}/pricey", $"{provider}/unpriced", $"{provider}/cheap" } } }));
        return (policy.GetProperty("policyId").GetGuid(), cheap, pricey, unpriced);
    }

    /// <summary>Simulates until host A's configuration snapshot has caught up with the catalog just written (NOTIFY, about a second).</summary>
    private static async Task<JsonElement> SimulateWhenCaughtUpAsync(AdminBrowser caller, Guid policyId, object body)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (true)
        {
            var response = await caller.SendJsonAsync(HttpMethod.Post, $"/admin/routing-policies/{policyId}/simulate", body);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var result = await AdminBrowser.JsonAsync(response);
            var known = result.GetProperty("candidates").EnumerateArray().All(c => c.GetProperty("providerName").ValueKind == JsonValueKind.String);
            var priced = result.GetProperty("candidates").EnumerateArray().Count(c => c.GetProperty("worstCaseEstimatedCost").ValueKind == JsonValueKind.Number) == 2;
            if ((known && priced) || Environment.TickCount64 > deadline)
            {
                return result;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    [HappyPath("POST /admin/routing-policies/{policyId:guid}/simulate")]
    public async Task A_viewer_sees_the_ranked_and_excluded_candidates_with_worst_case_cost_and_nothing_is_written()
    {
        using var owner = await fixture.OwnerAsync();
        var (policyId, cheap, pricey, unpriced) = await PolicyAsync(owner);
        using var viewer = await fixture.AdministratorAsync("viewer");
        var requestsBefore = await fixture.ScalarAsync("select count(*) from usage.inference_request");
        var versionBefore = await fixture.ScalarAsync("select version from config.config_version");

        var result = await SimulateWhenCaughtUpAsync(viewer, policyId, new { estimatedInputTokens = 1000, maxOutputTokens = 500 });
        var forTenant = await SimulateWhenCaughtUpAsync(owner, policyId, new { estimatedInputTokens = 1000, maxOutputTokens = 500, tenantId = fixture.TenantId });
        var outsideGrant = await viewer.SendJsonAsync(HttpMethod.Post, $"/admin/routing-policies/{policyId}/simulate", new { estimatedInputTokens = 1000, tenantId = fixture.TenantId });

        forTenant.GetProperty("candidates").GetArrayLength().ShouldBe(3);
        outsideGrant.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        result.GetProperty("version").GetInt32().ShouldBe(1);
        result.GetProperty("strategy").GetString().ShouldBe("cost");
        result.GetProperty("errorCode").ValueKind.ShouldBe(JsonValueKind.Null);
        result.GetProperty("currency").GetString().ShouldBe("USD");
        var candidates = result.GetProperty("candidates").EnumerateArray().ToList();
        candidates.Select(c => (c.GetProperty("providerModelId").GetGuid(), c.GetProperty("status").GetString()))
            .ShouldBe([(cheap, "eligible"), (pricey, "eligible"), (unpriced, "excluded")]);
        candidates[0].GetProperty("rank").GetInt32().ShouldBe(1);
        candidates[0].GetProperty("model").GetString().ShouldBe("cheap");
        candidates[0].GetProperty("circuitState").GetString().ShouldBe("closed");
        candidates[0].GetProperty("worstCaseEstimatedCost").GetDecimal().ShouldBe(0.002m);
        candidates[2].GetProperty("exclusionReason").GetString().ShouldBe("no_price");
        (await fixture.ScalarAsync("select count(*) from usage.inference_request")).ShouldBe(requestsBefore);
        (await fixture.ScalarAsync("select version from config.config_version")).ShouldBe(versionBefore);
    }

    [Fact]
    public async Task A_capability_no_candidate_has_is_reported_as_no_eligible_model_with_the_reasons()
    {
        using var owner = await fixture.OwnerAsync();
        var (policyId, _, _, _) = await PolicyAsync(owner);

        var result = await SimulateWhenCaughtUpAsync(owner, policyId, new { estimatedInputTokens = 10, requires = _jsonSchema });

        result.GetProperty("errorCode").GetString().ShouldBe("no_eligible_model");
        result.GetProperty("effectiveMaxOutputTokens").GetInt32().ShouldBe(1024);
        result.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("exclusionReason").GetString()).ShouldAllBe(r => r == "capability_missing:jsonSchema");
    }

    [Fact]
    public async Task An_invalid_body_is_400_validation_failed_and_an_unknown_policy_or_version_is_404()
    {
        using var owner = await fixture.OwnerAsync();
        var (policyId, _, _, _) = await PolicyAsync(owner);

        var invalid = await owner.SendJsonAsync(HttpMethod.Post, $"/admin/routing-policies/{policyId}/simulate", new { maxOutputTokens = 0, taskType = "Bad Type" });
        var unknownPolicy = await owner.SendJsonAsync(HttpMethod.Post, $"/admin/routing-policies/{Guid.CreateVersion7()}/simulate", new { estimatedInputTokens = 1 });
        var unknownVersion = await owner.SendJsonAsync(HttpMethod.Post, $"/admin/routing-policies/{policyId}/simulate", new { estimatedInputTokens = 1, version = 9 });

        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        invalid.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await AdminBrowser.JsonAsync(invalid);
        body.GetProperty("errorCode").GetString().ShouldBe("validation_failed");
        body.GetProperty("errors").EnumerateObject().Select(e => e.Name).ShouldBe(["estimatedInputTokens", "maxOutputTokens", "taskType"], ignoreOrder: true);
        unknownPolicy.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        unknownVersion.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
