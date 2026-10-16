using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.IntegrationTests.Infrastructure;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>The tenant-isolation matrix for the admin API (testing.md sections 2 and 4, security.md section 4).</summary>
public sealed class TenantIsolationTests(AdminFixture admin) : IClassFixture<AdminFixture>
{
    [Fact]
    public void Every_tenant_scoped_route_is_in_the_isolation_matrix()
    {
        // A route is tenant-scoped when it binds a tenant id or a child of one (an API Key id). Adding such an endpoint without a
        // matrix row fails here, like the happy-path gate fails for a route without a test. The fixture's own stand-in route is skipped.
        var scoped = admin.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => !e.RoutePattern.RawText!.StartsWith("/admin/_test/", StringComparison.Ordinal))
            .Where(e => e.RoutePattern.Parameters.Any(p => TenantIsolationMatrix.TenantScopedParameters.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"]).Select(method => $"{method} {e.RoutePattern.RawText}"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var covered = TenantIsolationMatrix.Routes.Where(r => !r.ByQuery).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        scoped.ShouldNotBeEmpty();
        scoped.Except(covered, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty("tenant-scoped routes missing from TenantIsolationMatrix.Routes");
        covered.Except(scoped, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty("matrix rows that match no tenant-scoped route");
    }

    [Fact]
    public async Task An_operator_granted_only_tenant_a_gets_404_for_every_tenant_scoped_route_of_tenant_b()
    {
        var foreign = await ForeignIdsAsync();
        using var outsider = await admin.SignedInAsync($"outsider-{Guid.CreateVersion7()}@example.com", "operator", admin.TenantA);

        await TenantIsolationMatrix.AssertAllForeignRoutesAreNotFoundAsync(outsider, foreign);

        await AssertTenantBUntouchedAsync(foreign);
    }

    [Fact]
    public async Task An_unknown_tenant_id_is_indistinguishable_from_a_foreign_one()
    {
        var foreign = await ForeignIdsAsync();
        using var outsider = await admin.SignedInAsync($"unknown-{Guid.CreateVersion7()}@example.com", "operator", admin.TenantA);

        foreach (var route in TenantIsolationMatrix.Routes)
        {
            var unknownIds = foreign with { TenantId = Guid.CreateVersion7() };
            using var unknown = await outsider.SendJsonAsync(route.Method, route.Path(unknownIds), route.Body?.Invoke(unknownIds));
            using var other = await outsider.SendJsonAsync(route.Method, route.Path(foreign), route.Body?.Invoke(foreign));
            unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound, route.Name);
            (await AdminBrowser.ErrorCodeAsync(unknown)).ShouldBe(await AdminBrowser.ErrorCodeAsync(other), route.Name);
        }
    }

    [Fact]
    public async Task A_foreign_child_id_under_the_callers_own_tenant_is_404_for_an_operator_and_for_the_owner()
    {
        var foreign = await ForeignIdsAsync();
        using var operatorOfA = await admin.SignedInAsync($"child-{Guid.CreateVersion7()}@example.com", "operator", admin.TenantA);
        using var owner = await admin.SignedInOwnerAsync();

        await TenantIsolationMatrix.AssertAllForeignChildIdsAreNotFoundAsync(operatorOfA, admin.TenantA, foreign.ApiKeyId);
        await TenantIsolationMatrix.AssertAllForeignChildIdsAreNotFoundAsync(owner, admin.TenantA, foreign.ApiKeyId);

        await AssertTenantBUntouchedAsync(foreign);
    }

    [Fact]
    public async Task A_viewer_granted_only_tenant_a_gets_404_for_the_read_routes_of_tenant_b()
    {
        var foreign = await ForeignIdsAsync();
        using var viewer = await admin.SignedInAsync($"viewer-out-{Guid.CreateVersion7()}@example.com", "viewer", admin.TenantA);

        foreach (var route in TenantIsolationMatrix.Routes.Where(r => r.Method == HttpMethod.Get))
        {
            var response = await viewer.GetAsync(route.Path(foreign));
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, route.Name);
        }

        (await viewer.GetAsync($"/admin/tenants/{admin.TenantA}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_tenant_list_never_contains_a_tenant_outside_the_grant()
    {
        using var operatorOfA = await admin.SignedInAsync($"list-{Guid.CreateVersion7()}@example.com", "operator", admin.TenantA);

        var body = await AdminBrowser.JsonAsync(await operatorOfA.GetAsync("/admin/tenants?limit=100"));

        body.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ShouldBe([admin.TenantA]);
    }

    private async Task<ForeignIds> ForeignIdsAsync()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var keyResponse = await owner.SendJsonAsync(HttpMethod.Post, $"/admin/tenants/{admin.TenantB}/api-keys", new { name = "b-key" });
        keyResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        var keyId = (await AdminBrowser.JsonAsync(keyResponse)).GetProperty("id").GetGuid();

        // A recorded Inference Request of Tenant B, as the request detail route reads it.
        var requestId = Guid.CreateVersion7();
        await admin.ScalarAsync(
            "insert into usage.inference_request (id, started_at, tenant_id, origin, api_key_id, requested_alias, streaming, status, attempt_count, had_fallback, input_bytes, usage_status, trace_id) " +
            $"values ('{requestId}', now(), '{admin.TenantB}', 'api', '{keyId}', 'balanced', false, 'succeeded', 0, false, 10, 'reported', '')");

        // A real Routing Policy, so the simulate route can only answer 404 because of the Tenant in its body.
        var policyId = Guid.CreateVersion7();
        await admin.ScalarAsync(
            $"insert into routing.routing_policy values ('{policyId}', 'iso-{policyId:N}', 1, now()); " +
            "insert into routing.routing_policy_version (policy_id, version, definition, resolved_candidates, created_at) " +
            $"values ('{policyId}', 1, '{{\"strategy\":\"static\",\"candidates\":[\"p/m\"]}}', '{{\"p/m\":\"{Guid.CreateVersion7()}\"}}', now())");
        return new ForeignIds(admin.TenantB, keyId, requestId, policyId);
    }

    private async Task AssertTenantBUntouchedAsync(ForeignIds foreign)
    {
        // Tenant B is active, has no limits row written by the intruder, and none of its keys were revoked.
        (await admin.ScalarAsync($"select status from identity.tenant where id = '{foreign.TenantId}'")).ShouldBe("active");
        (await admin.ScalarAsync($"select count(*) from identity.tenant_limits where tenant_id = '{foreign.TenantId}'")).ShouldBe("0");
        (await admin.ScalarAsync($"select count(*) from identity.api_key where tenant_id = '{foreign.TenantId}' and revoked_at is not null")).ShouldBe("0");
        (await admin.ScalarAsync($"select count(*) from identity.api_key where tenant_id = '{foreign.TenantId}' and name = 'intruder'")).ShouldBe("0");
    }
}

/// <summary>The inference side of the matrix: <c>/v1</c> never shows another Tenant's aliases or usage (testing.md section 2).</summary>
public sealed class TenantIsolationV1Tests(GatewayFixture gateway) : IClassFixture<GatewayFixture>
{
    private static StringContent Chat(string alias)
        => new(
            new JsonObject
            {
                ["model"] = alias,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }),
            }.ToJsonString(),
            Encoding.UTF8,
            "application/json");

    private async Task<(Guid TenantId, HttpClient Client)> TenantWithAccessAsync(string name, int requestsPerMinute, params string[] aliases)
    {
        var (tenantId, key) = await gateway.SeedTenantAsync(name, requestsPerMinute);
        await gateway.WriteConfigurationAsync($"update identity.tenant_limits set allowed_aliases = array[{string.Join(',', aliases.Select(a => $"'{a}'"))}] where tenant_id = '{tenantId}'");
        return (tenantId, gateway.Authenticated(key));
    }

    [Fact]
    public async Task Models_lists_only_the_aliases_the_tenants_model_access_allows()
    {
        var (_, a) = await TenantWithAccessAsync("iso-models-a", 100, GatewayFixture.ChatAlias);
        var (_, b) = await TenantWithAccessAsync("iso-models-b", 100, GatewayFixture.OllamaAlias, GatewayFixture.ResilientAlias);

        async Task<string[]> ListAsync(HttpClient client)
        {
            var body = await client.GetFromJsonAsync<JsonElement>("/v1/models", TestContext.Current.CancellationToken);
            return [.. body.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()!)];
        }

        (await ListAsync(a)).ShouldBe([GatewayFixture.ChatAlias]);
        (await ListAsync(b)).ShouldBe([GatewayFixture.OllamaAlias, GatewayFixture.ResilientAlias]);
    }

    [Fact]
    public async Task An_alias_only_another_tenant_may_use_is_403_model_not_allowed_and_leaks_nothing()
    {
        var (tenantA, a) = await TenantWithAccessAsync("iso-alias-a", 100, GatewayFixture.ChatAlias);
        var (tenantB, b) = await TenantWithAccessAsync("iso-alias-b", 100, GatewayFixture.OllamaAlias);

        var denied = await a.PostAsync("/v1/chat/completions", Chat(GatewayFixture.OllamaAlias), TestContext.Current.CancellationToken);
        var allowed = await b.PostAsync("/v1/chat/completions", Chat(GatewayFixture.OllamaAlias), TestContext.Current.CancellationToken);

        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AdminBrowser.ErrorCodeAsync(denied)).ShouldBe("model_not_allowed");
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await gateway.ScalarAsync($"select count(*) from usage.usage_record where tenant_id = '{tenantA}'")).ShouldBe("0");
        (await gateway.ScalarAsync($"select count(*) from usage.usage_record where tenant_id = '{tenantB}'")).ShouldBe("1");
    }

    [Fact]
    public async Task Responses_carry_only_the_callers_own_request_ids_limits_and_usage()
    {
        var (tenantA, a) = await TenantWithAccessAsync("iso-usage-a", 100, "*");
        var (tenantB, b) = await TenantWithAccessAsync("iso-usage-b", 7, "*");

        var forB = await b.PostAsync("/v1/chat/completions", Chat(GatewayFixture.ChatAlias), TestContext.Current.CancellationToken);
        var forA = await a.PostAsync("/v1/chat/completions", Chat(GatewayFixture.ChatAlias), TestContext.Current.CancellationToken);

        forA.StatusCode.ShouldBe(HttpStatusCode.OK);
        forB.StatusCode.ShouldBe(HttpStatusCode.OK);
        forA.Headers.GetValues("X-RateLimit-Limit").Single().ShouldBe("100");
        forB.Headers.GetValues("X-RateLimit-Limit").Single().ShouldBe("7");
        var requestIdA = forA.Headers.GetValues("X-Request-Id").Single();
        var requestIdB = forB.Headers.GetValues("X-Request-Id").Single();
        var bodyA = await forA.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        bodyA.ShouldNotContain(requestIdB);
        bodyA.ShouldNotContain(tenantB.ToString());
        (await gateway.ScalarAsync($"select tenant_id from usage.inference_request where id = '{requestIdA}'")).ShouldBe(tenantA.ToString());
        (await gateway.ScalarAsync($"select count(*) from usage.usage_record where tenant_id = '{tenantA}' and inference_request_id = '{requestIdB}'")).ShouldBe("0");
    }
}
