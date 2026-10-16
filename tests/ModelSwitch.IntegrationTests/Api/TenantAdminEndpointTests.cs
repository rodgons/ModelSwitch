using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.IntegrationTests.Infrastructure;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>Tenants, limits, model access and API Keys (api.md section 3): behaviour, audit, config version and the /v1 effects.</summary>
public sealed class TenantAdminEndpointTests(AdminFixture admin) : IClassFixture<AdminFixture>
{
    private async Task<long> ConfigVersionAsync() => long.Parse(await admin.ScalarAsync("select version from config.config_version"), System.Globalization.CultureInfo.InvariantCulture);

    private async Task<int> AuditCountAsync(string action, string targetId)
        => int.Parse(await admin.ScalarAsync($"select count(*) from identity.audit_record where action = '{action}' and target_id = '{targetId}'"), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<Guid> CreateTenantAsync(AdminBrowser browser, string? name = null)
    {
        var response = await browser.SendJsonAsync(HttpMethod.Post, "/admin/tenants", new { name = name ?? $"tenant-{Guid.CreateVersion7()}" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await AdminBrowser.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<(Guid Id, string Key)> CreateKeyAsync(AdminBrowser browser, Guid tenantId, object? body = null)
    {
        var response = await browser.SendJsonAsync(HttpMethod.Post, $"/admin/tenants/{tenantId}/api-keys", body ?? new { name = "ci" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var json = await AdminBrowser.JsonAsync(response);
        return (json.GetProperty("id").GetGuid(), json.GetProperty("key").GetString()!);
    }

    [Fact]
    [HappyPath("POST /admin/tenants")]
    public async Task An_operator_creates_a_tenant_that_is_audited_and_bumps_the_config_version()
    {
        using var op = await admin.SignedInAsync($"op-{Guid.CreateVersion7()}@example.com", "operator", allTenants: true);
        var before = await ConfigVersionAsync();

        var response = await op.SendJsonAsync(HttpMethod.Post, "/admin/tenants", new { name = "  Acme Corp  " });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await AdminBrowser.JsonAsync(response);
        var id = body.GetProperty("id").GetGuid();
        response.Headers.Location!.ToString().ShouldBe($"/admin/tenants/{id}");
        body.GetProperty("name").GetString().ShouldBe("Acme Corp");
        body.GetProperty("status").GetString().ShouldBe("active");
        (await AuditCountAsync("tenant.created", id.ToString())).ShouldBe(1);
        (await ConfigVersionAsync()).ShouldBe(before + 1);
    }

    [Fact]
    public async Task A_duplicate_tenant_name_is_a_409_and_writes_nothing()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var name = $"dup-{Guid.CreateVersion7()}";
        await CreateTenantAsync(owner, name);
        var before = await ConfigVersionAsync();

        var response = await owner.SendJsonAsync(HttpMethod.Post, "/admin/tenants", new { name });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ConfigVersionAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task An_operator_with_an_explicit_grant_cannot_create_a_tenant()
    {
        using var op = await admin.SignedInAsync($"granted-{Guid.CreateVersion7()}@example.com", "operator", admin.TenantA);

        var response = await op.SendJsonAsync(HttpMethod.Post, "/admin/tenants", new { name = $"nope-{Guid.CreateVersion7()}" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AdminBrowser.ErrorCodeAsync(response)).ShouldBe("forbidden");
    }

    [Fact]
    public async Task An_invalid_tenant_is_a_400_validation_problem_and_writes_nothing()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var before = await ConfigVersionAsync();

        var create = await owner.SendJsonAsync(HttpMethod.Post, "/admin/tenants", new { name = "  " });
        var patch = await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/tenants/{admin.TenantA}", new { name = "", status = "deleted" });

        foreach (var (response, fields) in new[] { (create, new[] { "name" }), (patch, ["name", "status"]) })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
            var body = await AdminBrowser.JsonAsync(response);
            body.GetProperty("errorCode").GetString().ShouldBe("validation_failed");
            foreach (var field in fields)
            {
                body.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue(field);
            }
        }

        (await ConfigVersionAsync()).ShouldBe(before);
    }

    [Fact]
    [HappyPath("GET /admin/tenants")]
    public async Task A_viewer_lists_only_the_granted_tenants_with_cursor_paging()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var first = await CreateTenantAsync(owner);
        var second = await CreateTenantAsync(owner);
        var third = await CreateTenantAsync(owner);
        await CreateTenantAsync(owner);
        using var viewer = await admin.SignedInAsync($"viewer-{Guid.CreateVersion7()}@example.com", "viewer", first, second, third);

        var page1 = await AdminBrowser.JsonAsync(await viewer.GetAsync("/admin/tenants?limit=2"));
        var cursor = page1.GetProperty("nextCursor").GetString();
        var page2 = await AdminBrowser.JsonAsync(await viewer.GetAsync($"/admin/tenants?limit=2&cursor={cursor}"));

        // Newest first, and the fourth tenant is outside the grant.
        page1.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ShouldBe([third, second]);
        page2.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ShouldBe([first]);
        page2.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        (await viewer.GetAsync("/admin/tenants?limit=0")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    [HappyPath("GET /admin/tenants/{tenantId:guid}")]
    public async Task The_tenant_detail_shows_limits_and_model_access_with_defaults()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var id = await CreateTenantAsync(owner);

        var body = await AdminBrowser.JsonAsync(await owner.GetAsync($"/admin/tenants/{id}"));

        body.GetProperty("limits").GetProperty("requestsPerMinute").ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("limits").GetProperty("budget").ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("modelAccess").GetProperty("allowedAliases").EnumerateArray().Select(a => a.GetString()).ShouldBe(["*"]);
        (await owner.GetAsync($"/admin/tenants/{Guid.CreateVersion7()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [HappyPath("PATCH /admin/tenants/{tenantId:guid}")]
    public async Task Patching_renames_and_suspends_with_their_own_audit_actions()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var id = await CreateTenantAsync(owner);
        var before = await ConfigVersionAsync();

        var rename = await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/tenants/{id}", new { name = $"renamed-{id}" });
        var suspend = await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/tenants/{id}", new { status = "suspended" });
        var reactivate = await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/tenants/{id}", new { status = "active" });

        (await AdminBrowser.JsonAsync(rename)).GetProperty("name").GetString().ShouldBe($"renamed-{id}");
        (await AdminBrowser.JsonAsync(suspend)).GetProperty("status").GetString().ShouldBe("suspended");
        (await AdminBrowser.JsonAsync(reactivate)).GetProperty("status").GetString().ShouldBe("active");
        (await AuditCountAsync("tenant.modified", id.ToString())).ShouldBe(1);
        (await AuditCountAsync("tenant.suspended", id.ToString())).ShouldBe(1);
        (await AuditCountAsync("tenant.reactivated", id.ToString())).ShouldBe(1);
        (await ConfigVersionAsync()).ShouldBe(before + 3);
    }

    [Fact]
    [HappyPath("PUT /admin/tenants/{tenantId:guid}/limits")]
    public async Task Limits_are_stored_feed_the_snapshot_source_and_set_the_budget()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var id = await CreateTenantAsync(owner);
        var before = await ConfigVersionAsync();

        var response = await owner.SendJsonAsync(
            HttpMethod.Put,
            $"/admin/tenants/{id}/limits",
            new { requestsPerMinute = 60, maxConcurrent = 4, maxRequestCost = 0.25m, budget = new { amount = 100.5m, period = "month" } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await AdminBrowser.JsonAsync(response);
        body.GetProperty("budget").GetProperty("amount").GetDecimal().ShouldBe(100.5m);
        var limits = (await admin.Services.GetRequiredService<ITenantLimitsSource>().LoadAsync(TestContext.Current.CancellationToken))[id];
        limits.RequestsPerMinute.ShouldBe(60);
        limits.MaxConcurrent.ShouldBe(4);
        limits.MaxRequestCost.ShouldBe(0.25m);
        limits.AllowedAliases.ShouldBe(["*"]);
        (await admin.ScalarAsync($"select limit_amount from usage.budget where tenant_id = '{id}'")).ShouldBe("100.5000000000");
        (await AuditCountAsync("tenant.limits_changed", id.ToString())).ShouldBe(1);
        (await ConfigVersionAsync()).ShouldBe(before + 1);

        // PUT replaces: a body without a Budget removes it, and no limit means unlimited.
        await owner.SendJsonAsync(HttpMethod.Put, $"/admin/tenants/{id}/limits", new { requestsPerMinute = 5 });
        (await admin.ScalarAsync($"select count(*) from usage.budget where tenant_id = '{id}'")).ShouldBe("0");
        var after = (await admin.Services.GetRequiredService<ITenantLimitsSource>().LoadAsync(TestContext.Current.CancellationToken))[id];
        after.MaxConcurrent.ShouldBeNull();
        after.RequestsPerMinute.ShouldBe(5);
    }

    [Fact]
    public async Task Invalid_limits_are_a_400_validation_problem_and_write_nothing()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var before = await ConfigVersionAsync();

        var response = await owner.SendJsonAsync(
            HttpMethod.Put,
            $"/admin/tenants/{admin.TenantA}/limits",
            new { requestsPerMinute = 0, maxConcurrent = -3, maxRequestCost = -1, budget = new { amount = 5, period = "week" } });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = (await AdminBrowser.JsonAsync(response)).GetProperty("errors");
        foreach (var field in new[] { "requestsPerMinute", "maxConcurrent", "maxRequestCost", "budget.period" })
        {
            errors.TryGetProperty(field, out _).ShouldBeTrue(field);
        }

        (await ConfigVersionAsync()).ShouldBe(before);
    }

    [Fact]
    [HappyPath("PUT /admin/tenants/{tenantId:guid}/model-access")]
    public async Task Model_access_feeds_the_snapshot_source_and_keeps_the_limits()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var id = await CreateTenantAsync(owner);
        await owner.SendJsonAsync(HttpMethod.Put, $"/admin/tenants/{id}/limits", new { requestsPerMinute = 7 });
        var before = await ConfigVersionAsync();

        var response = await owner.SendJsonAsync(HttpMethod.Put, $"/admin/tenants/{id}/model-access", new { allowedAliases = new List<string> { "fast", "balanced", "fast" } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var limits = (await admin.Services.GetRequiredService<ITenantLimitsSource>().LoadAsync(TestContext.Current.CancellationToken))[id];
        limits.AllowedAliases.ShouldBe(["balanced", "fast"]);
        limits.RequestsPerMinute.ShouldBe(7);
        (await AuditCountAsync("tenant.model_access_changed", id.ToString())).ShouldBe(1);
        (await ConfigVersionAsync()).ShouldBe(before + 1);
    }

    [Fact]
    public async Task Invalid_model_access_is_a_400_validation_problem_and_writes_nothing()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var before = await ConfigVersionAsync();

        foreach (var body in new object[] { new { allowedAliases = Array.Empty<string>() }, new { allowedAliases = new[] { "*", "fast" } }, new { allowedAliases = new[] { "Bad Alias" } }, new { } })
        {
            var response = await owner.SendJsonAsync(HttpMethod.Put, $"/admin/tenants/{admin.TenantA}/model-access", body);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await AdminBrowser.JsonAsync(response)).GetProperty("errors").TryGetProperty("allowedAliases", out _).ShouldBeTrue();
        }

        (await ConfigVersionAsync()).ShouldBe(before);
    }

    [Fact]
    [HappyPath("POST /admin/tenants/{tenantId:guid}/api-keys")]
    public async Task A_created_key_is_shown_once_never_stored_in_clear_and_works_on_v1()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var tenant = await CreateTenantAsync(owner);
        var before = await ConfigVersionAsync();

        var response = await owner.SendJsonAsync(HttpMethod.Post, $"/admin/tenants/{tenant}/api-keys", new { name = "ci", scopes = new List<string> { "models:read" } });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var body = await AdminBrowser.JsonAsync(response);
        var id = body.GetProperty("id").GetGuid();
        var key = body.GetProperty("key").GetString()!;
        key.Length.ShouldBe(56);
        body.GetProperty("keyPrefix").GetString().ShouldBe(key[..12]);
        body.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ShouldBe(["models:read"]);
        (await admin.ScalarAsync($"select count(*) from identity.api_key where id = '{id}' and key_hash <> convert_to('{key}', 'UTF8')")).ShouldBe("1");
        (await admin.ScalarAsync($"select details::text from identity.audit_record where action = 'api_key.created' and target_id = '{id}'")).ShouldNotContain(key);
        (await ConfigVersionAsync()).ShouldBe(before + 1);

        using var api = admin.NewApiClient(key);
        (await api.GetAsync("/v1/models", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The list never shows the raw key again.
        var list = await owner.GetAsync($"/admin/tenants/{tenant}/api-keys");
        (await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain(key);
    }

    [Fact]
    public async Task An_invalid_api_key_request_is_a_400_validation_problem_and_creates_nothing()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var before = await ConfigVersionAsync();

        var response = await owner.SendJsonAsync(
            HttpMethod.Post,
            $"/admin/tenants/{admin.TenantA}/api-keys",
            new { name = "", scopes = new List<string> { "root" }, expiresAt = "2020-01-01T00:00:00Z" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = (await AdminBrowser.JsonAsync(response)).GetProperty("errors");
        foreach (var field in new[] { "name", "scopes", "expiresAt" })
        {
            errors.TryGetProperty(field, out _).ShouldBeTrue(field);
        }

        (await ConfigVersionAsync()).ShouldBe(before);
    }

    [Fact]
    [HappyPath("GET /admin/tenants/{tenantId:guid}/api-keys")]
    public async Task Listing_keys_pages_newest_first_without_secrets()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var tenant = await CreateTenantAsync(owner);
        var first = await CreateKeyAsync(owner, tenant, new { name = "first" });
        var second = await CreateKeyAsync(owner, tenant, new { name = "second" });
        var third = await CreateKeyAsync(owner, tenant, new { name = "third" });

        var page1 = await AdminBrowser.JsonAsync(await owner.GetAsync($"/admin/tenants/{tenant}/api-keys?limit=2"));
        var page2 = await AdminBrowser.JsonAsync(await owner.GetAsync($"/admin/tenants/{tenant}/api-keys?limit=2&cursor={page1.GetProperty("nextCursor").GetString()}"));

        page1.GetProperty("items").EnumerateArray().Select(k => k.GetProperty("id").GetGuid()).ShouldBe([third.Id, second.Id]);
        page2.GetProperty("items").EnumerateArray().Select(k => k.GetProperty("id").GetGuid()).ShouldBe([first.Id]);
        var item = page1.GetProperty("items")[0];
        item.TryGetProperty("key", out _).ShouldBeFalse();
        item.TryGetProperty("keyHash", out _).ShouldBeFalse();
        item.GetProperty("revokedAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    [HappyPath("DELETE /admin/tenants/{tenantId:guid}/api-keys/{keyId:guid}")]
    public async Task A_revoked_key_gets_401_on_v1_at_once_and_revoking_twice_audits_once()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var tenant = await CreateTenantAsync(owner);
        var (id, key) = await CreateKeyAsync(owner, tenant);
        using var api = admin.NewApiClient(key);
        (await api.GetAsync("/v1/models", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = await ConfigVersionAsync();

        var revoke = await owner.SendJsonAsync(HttpMethod.Delete, $"/admin/tenants/{tenant}/api-keys/{id}");
        var again = await owner.SendJsonAsync(HttpMethod.Delete, $"/admin/tenants/{tenant}/api-keys/{id}");

        revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        again.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await api.GetAsync("/v1/models", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuditCountAsync("api_key.revoked", id.ToString())).ShouldBe(1);
        (await ConfigVersionAsync()).ShouldBe(before + 1);
        var list = await AdminBrowser.JsonAsync(await owner.GetAsync($"/admin/tenants/{tenant}/api-keys"));
        list.GetProperty("items")[0].GetProperty("revokedAt").ValueKind.ShouldBe(JsonValueKind.String);
        (await owner.SendJsonAsync(HttpMethod.Delete, $"/admin/tenants/{tenant}/api-keys/{Guid.CreateVersion7()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_suspended_tenant_gets_403_on_v1_and_is_served_again_when_reactivated()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var tenant = await CreateTenantAsync(owner);
        var (_, key) = await CreateKeyAsync(owner, tenant);
        using var api = admin.NewApiClient(key);
        (await api.GetAsync("/v1/models", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/tenants/{tenant}", new { status = "suspended" });
        var suspended = await api.GetAsync("/v1/models", TestContext.Current.CancellationToken);
        await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/tenants/{tenant}", new { status = "active" });
        var reactivated = await api.GetAsync("/v1/models", TestContext.Current.CancellationToken);

        suspended.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AdminBrowser.ErrorCodeAsync(suspended)).ShouldBe("tenant_suspended");
        reactivated.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_viewer_can_read_but_every_write_is_403()
    {
        using var viewer = await admin.SignedInAsync($"ro-{Guid.CreateVersion7()}@example.com", "viewer", admin.TenantA);
        var before = await ConfigVersionAsync();

        (await viewer.GetAsync("/admin/tenants")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await viewer.GetAsync($"/admin/tenants/{admin.TenantA}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await viewer.GetAsync($"/admin/tenants/{admin.TenantA}/api-keys")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var writes = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Post, "/admin/tenants", new { name = "viewer-made" }),
            (HttpMethod.Patch, $"/admin/tenants/{admin.TenantA}", new { status = "suspended" }),
            (HttpMethod.Put, $"/admin/tenants/{admin.TenantA}/limits", new { requestsPerMinute = 1 }),
            (HttpMethod.Put, $"/admin/tenants/{admin.TenantA}/model-access", new { allowedAliases = new[] { "*" } }),
            (HttpMethod.Post, $"/admin/tenants/{admin.TenantA}/api-keys", new { name = "viewer-key" }),
            (HttpMethod.Delete, $"/admin/tenants/{admin.TenantA}/api-keys/{Guid.CreateVersion7()}", null),
        };
        foreach (var (method, path, body) in writes)
        {
            var response = await viewer.SendJsonAsync(method, path, body);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{method} {path}");
        }

        (await ConfigVersionAsync()).ShouldBe(before);
    }
}
