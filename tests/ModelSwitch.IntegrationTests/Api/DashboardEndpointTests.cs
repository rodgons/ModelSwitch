using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.SharedKernel.Jobs;
using ModelSwitch.Usage;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>The dashboard's read endpoints (api.md section 3, "Requests and usage") over real recorded traffic.</summary>
public sealed class DashboardEndpointTests(GatewayFixture gateway) : IClassFixture<GatewayFixture>
{
    private static StringContent Chat(string alias)
        => new(
            new JsonObject
            {
                ["model"] = alias,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello dashboard" }),
            }.ToJsonString(),
            Encoding.UTF8,
            "application/json");

    private async Task<string> CompleteAsync(string alias = GatewayFixture.ChatAlias)
    {
        var response = await gateway.Client.PostAsync("/v1/chat/completions", Chat(alias), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var requestId = response.Headers.GetValues("X-Request-Id").Single();

        // Settlement runs after the response is written.
        var deadline = Environment.TickCount64 + 5_000;
        while (await gateway.ScalarAsync($"select status from usage.inference_request where id = '{requestId}'") == "running" && Environment.TickCount64 < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        return requestId;
    }

    private static async Task<JsonElement> OkJsonAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await AdminBrowser.JsonAsync(response);
    }

    [Fact]
    [HappyPath("GET /admin/requests")]
    public async Task The_request_list_filters_names_the_model_and_pages_with_a_cursor()
    {
        var first = await CompleteAsync(GatewayFixture.OllamaAlias);
        var second = await CompleteAsync(GatewayFixture.OllamaAlias);
        using var owner = await gateway.OwnerBrowserAsync();

        var page1 = await OkJsonAsync(await owner.GetAsync($"/admin/requests?alias={GatewayFixture.OllamaAlias}&tenantId={gateway.TenantId}&limit=1"));
        var cursor = page1.GetProperty("nextCursor").GetString()!;
        var page2 = await OkJsonAsync(await owner.GetAsync($"/admin/requests?alias={GatewayFixture.OllamaAlias}&tenantId={gateway.TenantId}&limit=1&cursor={cursor}"));

        var newest = page1.GetProperty("items").EnumerateArray().Single();
        newest.GetProperty("id").GetString().ShouldBe(second);
        newest.GetProperty("status").GetString().ShouldBe("succeeded");
        newest.GetProperty("selectedModel").GetProperty("providerName").GetString().ShouldBe("mock-ollama");
        newest.GetProperty("selectedModel").GetProperty("name").GetString().ShouldBe(GatewayFixture.PrimaryModel);
        page2.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ShouldContain(first);
    }

    [Fact]
    [HappyPath("GET /admin/requests/{requestId:guid}")]
    public async Task The_request_detail_has_the_routing_decision_attempts_and_usage_records()
    {
        var requestId = await CompleteAsync();
        using var owner = await gateway.OwnerBrowserAsync();

        var detail = await OkJsonAsync(await owner.GetAsync($"/admin/requests/{requestId}"));

        detail.GetProperty("request").GetProperty("requestedAlias").GetString().ShouldBe(GatewayFixture.ChatAlias);
        detail.GetProperty("routingDecision").ValueKind.ShouldBe(JsonValueKind.Object);
        detail.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
        var attempt = detail.GetProperty("attempts").EnumerateArray().Single();
        attempt.GetProperty("kind").GetString().ShouldBe("primary");
        attempt.GetProperty("model").GetProperty("providerName").GetString().ShouldBe("mock-openai");
        var record = detail.GetProperty("usageRecords").EnumerateArray().Single();
        record.GetProperty("usageSource").GetString().ShouldBe("reported");
        record.GetProperty("totalCost").GetDecimal().ShouldBeGreaterThan(0);

        (await owner.GetAsync($"/admin/requests/{Guid.CreateVersion7()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [HappyPath("GET /admin/usage")]
    public async Task Usage_groups_tokens_and_estimated_cost_by_provider_and_alias()
    {
        await CompleteAsync();
        await CompleteAsync();
        using var owner = await gateway.OwnerBrowserAsync();

        var report = await OkJsonAsync(await owner.GetAsync($"/admin/usage?groupBy=provider,alias&interval=day&alias={GatewayFixture.ChatAlias}"));

        report.GetProperty("groupBy").EnumerateArray().Select(g => g.GetString()).ShouldBe(["provider", "alias"]);
        var row = report.GetProperty("items").EnumerateArray().Single();
        row.GetProperty("providerName").GetString().ShouldBe("mock-openai");
        row.GetProperty("alias").GetString().ShouldBe(GatewayFixture.ChatAlias);
        row.GetProperty("model").ValueKind.ShouldBe(JsonValueKind.Null);
        row.GetProperty("requests").GetInt64().ShouldBeGreaterThanOrEqualTo(2);
        row.GetProperty("inputTokens").GetInt64().ShouldBeGreaterThan(0);
        row.GetProperty("estimatedCost").GetDecimal().ShouldBeGreaterThan(0);
        row.GetProperty("currency").GetString().ShouldBe("USD");
    }

    [Fact]
    [HappyPath("GET /admin/overview")]
    public async Task The_overview_has_tiles_a_series_and_recent_failures()
    {
        await CompleteAsync();
        var failed = await gateway.Client.PostAsync("/v1/chat/completions", Chat("no-such-alias"), TestContext.Current.CancellationToken);
        failed.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var owner = await gateway.OwnerBrowserAsync();

        var overview = await OkJsonAsync(await owner.GetAsync($"/admin/overview?tenantId={gateway.TenantId}"));

        var totals = overview.GetProperty("totals");
        totals.GetProperty("requests").GetInt64().ShouldBeGreaterThanOrEqualTo(2);
        totals.GetProperty("failedRequests").GetInt64().ShouldBeGreaterThanOrEqualTo(1);
        totals.GetProperty("exact").GetBoolean().ShouldBeTrue();
        totals.GetProperty("latencyP95Ms").GetInt32().ShouldBeGreaterThanOrEqualTo(0);
        overview.GetProperty("stepSeconds").GetInt32().ShouldBe(3600);
        overview.GetProperty("series").GetArrayLength().ShouldBeGreaterThan(0);
        overview.GetProperty("recentFailures").EnumerateArray().Select(f => f.GetProperty("id").GetString())
            .ShouldContain(failed.Headers.GetValues("X-Request-Id").Single());
    }

    [Fact]
    public async Task Over_a_week_the_rollup_and_the_raw_edges_give_the_same_totals_as_the_exact_day()
    {
        await CompleteAsync();
        await using (var worker = ModuleServices.ForJobs(gateway.ConnectionString, configure: services => services.AddUsageModule()))
        {
            while (await worker.GetRequiredService<OutboxRelay>().RelayAsync(100, CancellationToken.None) > 0)
            {
            }
        }

        using var owner = await gateway.OwnerBrowserAsync();
        var now = DateTimeOffset.UtcNow;
        var to = now.AddMinutes(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var day = await OkJsonAsync(await owner.GetAsync($"/admin/overview?tenantId={gateway.TenantId}&to={Uri.EscapeDataString(to)}"));
        var week = await OkJsonAsync(await owner.GetAsync(
            $"/admin/overview?tenantId={gateway.TenantId}&from={Uri.EscapeDataString(now.AddDays(-7).ToString("O", System.Globalization.CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(to)}"));

        week.GetProperty("totals").GetProperty("exact").GetBoolean().ShouldBeFalse();
        week.GetProperty("totals").GetProperty("requests").GetInt64().ShouldBe(day.GetProperty("totals").GetProperty("requests").GetInt64());
        week.GetProperty("totals").GetProperty("estimatedCost").GetDecimal().ShouldBe(day.GetProperty("totals").GetProperty("estimatedCost").GetDecimal());
    }

    [Fact]
    [HappyPath("GET /admin/provider-health")]
    public async Task Provider_health_lists_every_provider_model_with_recent_attempts()
    {
        await CompleteAsync();
        using var owner = await gateway.OwnerBrowserAsync();

        var health = await OkJsonAsync(await owner.GetAsync("/admin/provider-health"));

        var echo = health.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("model").GetProperty("providerName").GetString() == "mock-openai"
                && i.GetProperty("model").GetProperty("name").GetString() == GatewayFixture.PrimaryModel);
        echo.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        echo.GetProperty("state").GetString().ShouldBeOneOf("closed", "unknown");
        echo.GetProperty("recent").GetProperty("attempts").GetInt64().ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task An_invalid_query_is_400_validation_failed_with_one_error_per_parameter()
    {
        using var owner = await gateway.OwnerBrowserAsync();

        var response = await owner.GetAsync("/admin/requests?status=done&limit=0");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await AdminBrowser.JsonAsync(response);
        body.GetProperty("errorCode").GetString().ShouldBe("validation_failed");
        body.GetProperty("errors").EnumerateObject().Select(e => e.Name).ShouldBe(["status", "limit"], ignoreOrder: true);
    }

    [Fact]
    public async Task The_overview_separates_api_traffic_from_evaluation_traffic()
    {
        await CompleteAsync();
        using var owner = await gateway.OwnerBrowserAsync();

        var api = await OkJsonAsync(await owner.GetAsync($"/admin/overview?tenantId={gateway.TenantId}&origin=api"));
        var evaluation = await OkJsonAsync(await owner.GetAsync($"/admin/overview?tenantId={gateway.TenantId}&origin=evaluation"));
        var invalid = await owner.GetAsync("/admin/overview?origin=batch");

        api.GetProperty("totals").GetProperty("requests").GetInt64().ShouldBeGreaterThanOrEqualTo(1);
        evaluation.GetProperty("totals").GetProperty("requests").GetInt64().ShouldBe(0);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AdminBrowser.JsonAsync(invalid)).GetProperty("errors").EnumerateObject().Select(e => e.Name).ShouldBe(["origin"]);
    }

    [Fact]
    public async Task An_administrator_without_a_session_is_401()
        => (await gateway.Anonymous.GetAsync("/admin/requests", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
}
