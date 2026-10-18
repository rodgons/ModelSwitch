using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.Configuration.Contracts;
using ModelSwitch.IntegrationTests.Infrastructure;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>The Provider, Provider Model, Price, Routing Policy and alias admin endpoints (docs/api.md section 3).</summary>
public sealed class CatalogAdminEndpointTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string _secret = "sk-CANARY-admin-9a8b7c";

    private const string _capabilities =
        """{"streaming":true,"jsonObject":true,"jsonSchema":true,"contextWindow":128000,"maxOutputTokens":16384,"tokenizer":"o200k_base","parameters":["temperature","top_p"]}""";

    private static string Unique(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}"[..30];

    private static JsonElement Capabilities() => JsonDocument.Parse(_capabilities).RootElement;

    private async Task<Guid> CreateProviderAsync(AdminBrowser owner, string? name = null, object? credential = null)
    {
        var response = await owner.SendJsonAsync(HttpMethod.Post, "/admin/providers", new { name = name ?? Unique("prov"), adapterType = "openai_compatible", endpoint = fixture.OpenAiEndpoint, credential });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await AdminBrowser.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateModelAsync(AdminBrowser owner, Guid providerId, string identifier)
    {
        var response = await owner.SendJsonAsync(HttpMethod.Post, $"/admin/providers/{providerId}/models", new { providerModelId = identifier, displayName = identifier, capabilities = Capabilities() });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await AdminBrowser.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task AddPriceAsync(AdminBrowser owner, Guid modelId, decimal input = 1, decimal output = 2)
    {
        var response = await owner.SendJsonAsync(HttpMethod.Post, $"/admin/models/{modelId}/prices", new { inputPerMillion = input, outputPerMillion = output });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A provider with one priced model; returns the provider name and the model id.</summary>
    private async Task<(string Provider, Guid ModelId)> PricedModelAsync(AdminBrowser owner, string identifier = "mock-echo")
    {
        var name = Unique("p");
        var providerId = await CreateProviderAsync(owner, name);
        var modelId = await CreateModelAsync(owner, providerId, identifier);
        await AddPriceAsync(owner, modelId);
        return (name, modelId);
    }

    private static object StaticPolicy(params string[] candidates) => new { strategy = "static", candidates };

    private static async Task<Guid> CreatePolicyAsync(AdminBrowser owner, string name, params string[] candidates)
    {
        var response = await owner.SendJsonAsync(HttpMethod.Post, "/admin/routing-policies", new { name, definition = StaticPolicy(candidates) });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await AdminBrowser.JsonAsync(response)).GetProperty("policyId").GetGuid();
    }

    private static async Task<JsonElement> ErrorsAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await AdminBrowser.JsonAsync(response);
        body.GetProperty("errorCode").GetString().ShouldBe("validation_failed");
        return body.GetProperty("errors");
    }

    // ---- Providers ---------------------------------------------------------------------------------------------

    [Fact]
    [HappyPath("POST /admin/providers")]
    public async Task An_owner_creates_a_provider_with_a_write_only_secret_and_it_is_audited_and_bumps_the_config_version()
    {
        using var owner = await fixture.OwnerAsync();
        var name = Unique("secret");
        var versionBefore = long.Parse(await fixture.ScalarAsync("select version from config.config_version"));

        var response = await owner.SendJsonAsync(HttpMethod.Post, "/admin/providers", new { name, adapterType = "openai_compatible", endpoint = fixture.OpenAiEndpoint, credential = new { secret = _secret } });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.ShouldNotContain(_secret);
        var body = JsonDocument.Parse(text).RootElement;
        body.GetProperty("credential").GetProperty("kind").GetString().ShouldBe("db");
        body.GetProperty("credential").GetProperty("configured").GetBoolean().ShouldBeTrue();
        response.Headers.Location!.ToString().ShouldBe($"/admin/providers/{body.GetProperty("id").GetGuid()}");

        long.Parse(await fixture.ScalarAsync("select version from config.config_version")).ShouldBeGreaterThan(versionBefore);
        var id = body.GetProperty("id").GetGuid();
        (await fixture.ScalarAsync($"select count(*) from identity.audit_record where target_id = '{id}' and action = 'provider.created'")).ShouldBe("1");
        var audit = await fixture.ScalarAsync($"select details::text from identity.audit_record where target_id = '{id}' and action = 'provider.secret_replaced'");
        audit.ShouldNotContain(_secret);
        (await fixture.ScalarAsync("select count(*) from identity.audit_record where details::text like '%" + _secret + "%'")).ShouldBe("0");
    }

    [Fact]
    [HappyPath("GET /admin/providers")]
    public async Task Providers_list_with_cursor_pagination_and_never_show_secrets()
    {
        using var owner = await fixture.OwnerAsync();
        await CreateProviderAsync(owner, credential: new { secret = _secret });
        await CreateProviderAsync(owner, credential: new { envVar = "SOME_API_KEY" });

        var first = await AdminBrowser.JsonAsync(await owner.GetAsync("/admin/providers?limit=1"));
        first.GetProperty("items").GetArrayLength().ShouldBe(1);
        var cursor = first.GetProperty("nextCursor").GetString();
        cursor.ShouldNotBeNullOrEmpty();
        var second = await owner.GetAsync($"/admin/providers?limit=1&cursor={Uri.EscapeDataString(cursor!)}");
        var secondItem = (await AdminBrowser.JsonAsync(second)).GetProperty("items")[0];
        secondItem.GetProperty("id").GetGuid().ShouldNotBe(first.GetProperty("items")[0].GetProperty("id").GetGuid());

        var all = await (await owner.GetAsync("/admin/providers?limit=200")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        all.ShouldNotContain(_secret);
        all.ShouldNotContain("SOME_API_KEY");
        all.ShouldNotContain("credentialReference");
    }

    [Fact]
    [HappyPath("GET /admin/providers/{providerId:guid}")]
    public async Task A_provider_reads_back_and_an_unknown_one_is_404()
    {
        using var owner = await fixture.OwnerAsync();
        var id = await CreateProviderAsync(owner);

        var found = await owner.GetAsync($"/admin/providers/{id}");
        var missing = await owner.GetAsync($"/admin/providers/{Guid.CreateVersion7()}");

        found.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AdminBrowser.JsonAsync(found)).GetProperty("credential").GetProperty("kind").GetString().ShouldBe("none");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [HappyPath("PATCH /admin/providers/{providerId:guid}")]
    public async Task Patching_disables_a_provider_and_replacing_the_secret_is_audited_without_the_secret()
    {
        using var owner = await fixture.OwnerAsync();
        var id = await CreateProviderAsync(owner, credential: new { secret = _secret });

        var disabled = await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/providers/{id}", new { enabled = false });
        var replaced = await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/providers/{id}", new { credential = new { secret = _secret + "-2" } });

        disabled.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AdminBrowser.JsonAsync(disabled)).GetProperty("enabled").GetBoolean().ShouldBeFalse();
        replaced.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await replaced.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain(_secret);
        (await fixture.ScalarAsync($"select count(*) from identity.audit_record where target_id = '{id}' and action = 'provider.modified'")).ShouldBe("1");
        (await fixture.ScalarAsync($"select count(*) from identity.audit_record where target_id = '{id}' and action = 'provider.secret_replaced'")).ShouldBe("2");
        (await fixture.ScalarAsync($"select count(*) from registry.provider_secret where provider_id = '{id}'")).ShouldBe("1");

        var switched = await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/providers/{id}", new { credential = new { envVar = "SOME_API_KEY" } });
        switched.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fixture.ScalarAsync($"select count(*) from registry.provider_secret where provider_id = '{id}'")).ShouldBe("0");
    }

    [Fact]
    [HappyPath("POST /admin/providers/{providerId:guid}/connectivity-check")]
    public async Task A_connectivity_check_reports_reachability_and_latency()
    {
        using var owner = await fixture.OwnerAsync();
        var id = await CreateProviderAsync(owner);

        var response = await owner.SendJsonAsync(HttpMethod.Post, $"/admin/providers/{id}/connectivity-check");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await AdminBrowser.JsonAsync(response);
        body.GetProperty("reachable").GetBoolean().ShouldBeTrue();
        body.GetProperty("latencyMs").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
        (await owner.SendJsonAsync(HttpMethod.Post, $"/admin/providers/{Guid.CreateVersion7()}/connectivity-check")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("https://169.254.169.254")]
    [InlineData("http://127.0.0.2:9999")]
    [InlineData("https://localhost")]
    [InlineData("http://10.0.0.5")]
    [InlineData("ftp://example.com")]
    public async Task A_provider_endpoint_that_reaches_the_gateways_own_network_is_rejected_on_create_and_on_patch(string endpoint)
    {
        using var owner = await fixture.OwnerAsync();

        var create = await owner.SendJsonAsync(HttpMethod.Post, "/admin/providers", new { name = Unique("ssrf"), adapterType = "openai_compatible", endpoint });
        var id = await CreateProviderAsync(owner);
        var patch = await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/providers/{id}", new { endpoint });

        (await ErrorsAsync(create)).TryGetProperty("endpoint", out _).ShouldBeTrue();
        (await ErrorsAsync(patch)).TryGetProperty("endpoint", out _).ShouldBeTrue();
        (await fixture.ScalarAsync($"select endpoint from registry.provider where id = '{id}'")).ShouldBe(fixture.OpenAiEndpoint);
    }

    [Fact]
    public async Task Invalid_provider_bodies_are_400_validation_problems_and_a_duplicate_name_is_409()
    {
        using var owner = await fixture.OwnerAsync();
        var name = Unique("dup");
        await CreateProviderAsync(owner, name);
        var id = await CreateProviderAsync(owner);

        var create = await ErrorsAsync(await owner.SendJsonAsync(HttpMethod.Post, "/admin/providers", new { name = "Bad/Name", adapterType = "gpt", endpoint = "", requestTimeoutMs = 5, credential = new { secret = "x", envVar = "Y" } }));
        var patchEmpty = await ErrorsAsync(await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/providers/{id}", new { }));
        var patchBad = await ErrorsAsync(await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/providers/{id}", new { requestTimeoutMs = 1, credential = new { envVar = "MODELSWITCH__Security__MasterKey" } }));
        var duplicate = await owner.SendJsonAsync(HttpMethod.Post, "/admin/providers", new { name, adapterType = "ollama", endpoint = fixture.OpenAiEndpoint });

        foreach (var field in new[] { "name", "adapterType", "endpoint", "requestTimeoutMs", "credential" })
        {
            create.TryGetProperty(field, out _).ShouldBeTrue(field);
        }

        patchEmpty.TryGetProperty("body", out _).ShouldBeTrue();
        patchBad.TryGetProperty("requestTimeoutMs", out _).ShouldBeTrue();
        patchBad.TryGetProperty("credential.envVar", out _).ShouldBeTrue();
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/providers/{Guid.CreateVersion7()}", new { enabled = false })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- Provider Models ---------------------------------------------------------------------------------------

    [Fact]
    [HappyPath("POST /admin/providers/{providerId:guid}/models")]
    public async Task An_owner_registers_a_provider_model_with_capabilities()
    {
        using var owner = await fixture.OwnerAsync();
        var providerId = await CreateProviderAsync(owner);

        var response = await owner.SendJsonAsync(HttpMethod.Post, $"/admin/providers/{providerId}/models", new { providerModelId = "gpt-x", displayName = "GPT X", capabilities = Capabilities(), enabled = true });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await AdminBrowser.JsonAsync(response);
        body.GetProperty("providerModelId").GetString().ShouldBe("gpt-x");
        body.GetProperty("capabilities").GetProperty("contextWindow").GetInt32().ShouldBe(128000);
        (await fixture.ScalarAsync($"select count(*) from identity.audit_record where action = 'provider_model.created' and target_id = '{body.GetProperty("id").GetGuid()}'")).ShouldBe("1");
        (await owner.SendJsonAsync(HttpMethod.Post, $"/admin/providers/{providerId}/models", new { providerModelId = "gpt-x", capabilities = Capabilities() })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await owner.SendJsonAsync(HttpMethod.Post, $"/admin/providers/{Guid.CreateVersion7()}/models", new { providerModelId = "gpt-y", capabilities = Capabilities() })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [HappyPath("GET /admin/providers/{providerId:guid}/models")]
    public async Task A_providers_models_list()
    {
        using var owner = await fixture.OwnerAsync();
        var providerId = await CreateProviderAsync(owner);
        await CreateModelAsync(owner, providerId, "m1");
        await CreateModelAsync(owner, providerId, "m2");

        var page = await AdminBrowser.JsonAsync(await owner.GetAsync($"/admin/providers/{providerId}/models?limit=1"));
        var rest = await AdminBrowser.JsonAsync(await owner.GetAsync($"/admin/providers/{providerId}/models?limit=1&cursor={Uri.EscapeDataString(page.GetProperty("nextCursor").GetString()!)}"));

        page.GetProperty("items").GetArrayLength().ShouldBe(1);
        rest.GetProperty("items").GetArrayLength().ShouldBe(1);
        rest.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        (await owner.GetAsync($"/admin/providers/{Guid.CreateVersion7()}/models")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [HappyPath("PATCH /admin/models/{modelId:guid}")]
    public async Task Patching_a_model_changes_capabilities_and_the_enabled_flag_with_an_audit_record()
    {
        using var owner = await fixture.OwnerAsync();
        var modelId = await CreateModelAsync(owner, await CreateProviderAsync(owner), "patch-me");

        var response = await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/models/{modelId}", new { enabled = false, capabilities = new { streaming = false, contextWindow = 8000 } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await AdminBrowser.JsonAsync(response);
        body.GetProperty("enabled").GetBoolean().ShouldBeFalse();
        body.GetProperty("capabilities").GetProperty("contextWindow").GetInt32().ShouldBe(8000);
        (await fixture.ScalarAsync($"select count(*) from identity.audit_record where action = 'provider_model.modified' and target_id = '{modelId}'")).ShouldBe("1");
        (await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/models/{Guid.CreateVersion7()}", new { enabled = true })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Invalid_model_bodies_are_400_validation_problems_with_a_field_per_problem()
    {
        using var owner = await fixture.OwnerAsync();
        var providerId = await CreateProviderAsync(owner);
        var modelId = await CreateModelAsync(owner, providerId, "validate-me");

        var create = await ErrorsAsync(await owner.SendJsonAsync(HttpMethod.Post, $"/admin/providers/{providerId}/models", new { providerModelId = "has space", capabilities = new { contextWindw = 5, tokenizer = "gpt2", maxOutputTokens = -1 } }));
        var patchEmpty = await ErrorsAsync(await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/models/{modelId}", new { }));
        var patchBad = await ErrorsAsync(await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/models/{modelId}", new { capabilities = new { contextWindow = 100, maxOutputTokens = 200 } }));

        create.TryGetProperty("providerModelId", out _).ShouldBeTrue();
        create.TryGetProperty("capabilities.contextWindw", out _).ShouldBeTrue();
        create.TryGetProperty("capabilities.tokenizer", out _).ShouldBeTrue();
        create.TryGetProperty("capabilities.maxOutputTokens", out _).ShouldBeTrue();
        patchEmpty.TryGetProperty("body", out _).ShouldBeTrue();
        patchBad.TryGetProperty("capabilities.maxOutputTokens", out _).ShouldBeTrue();
    }

    // ---- Prices ------------------------------------------------------------------------------------------------

    [Fact]
    [HappyPath("POST /admin/models/{modelId:guid}/prices")]
    public async Task A_new_price_is_a_new_row_and_the_old_price_row_is_untouched()
    {
        using var owner = await fixture.OwnerAsync();
        var modelId = await CreateModelAsync(owner, await CreateProviderAsync(owner), "priced");
        await AddPriceAsync(owner, modelId, input: 1, output: 2);
        var firstId = await fixture.ScalarAsync($"select id from registry.model_price where provider_model_id = '{modelId}'");

        var future = DateTimeOffset.UtcNow.AddDays(1);
        var response = await owner.SendJsonAsync(HttpMethod.Post, $"/admin/models/{modelId}/prices", new { inputPerMillion = 3, cachedInputPerMillion = 0.5, outputPerMillion = 6, effectiveFrom = future });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await AdminBrowser.JsonAsync(response);
        body.GetProperty("inForce").GetBoolean().ShouldBeFalse();
        body.GetProperty("cachedInputPerMillion").GetDecimal().ShouldBe(0.5m);
        (await fixture.ScalarAsync($"select count(*) from registry.model_price where provider_model_id = '{modelId}'")).ShouldBe("2");
        (await fixture.ScalarAsync($"select input_per_million from registry.model_price where id = '{firstId}'")).ShouldBe("1.0000000000");
        (await fixture.ScalarAsync($"select count(*) from identity.audit_record where action = 'model_price.created' and details::text like '%{modelId}%'")).ShouldBe("2");
    }

    [Fact]
    [HappyPath("GET /admin/models/{modelId:guid}/prices")]
    public async Task Price_history_lists_newest_first_and_marks_the_price_in_force()
    {
        using var owner = await fixture.OwnerAsync();
        var modelId = await CreateModelAsync(owner, await CreateProviderAsync(owner), "history");
        await AddPriceAsync(owner, modelId, input: 1);
        (await owner.SendJsonAsync(HttpMethod.Post, $"/admin/models/{modelId}/prices", new { inputPerMillion = 9, outputPerMillion = 9, effectiveFrom = DateTimeOffset.UtcNow.AddDays(2) })).StatusCode.ShouldBe(HttpStatusCode.Created);

        var page = await AdminBrowser.JsonAsync(await owner.GetAsync($"/admin/models/{modelId}/prices?limit=1"));
        var rest = await AdminBrowser.JsonAsync(await owner.GetAsync($"/admin/models/{modelId}/prices?limit=1&cursor={Uri.EscapeDataString(page.GetProperty("nextCursor").GetString()!)}"));

        page.GetProperty("items")[0].GetProperty("inputPerMillion").GetDecimal().ShouldBe(9m);
        page.GetProperty("items")[0].GetProperty("inForce").GetBoolean().ShouldBeFalse();
        rest.GetProperty("items")[0].GetProperty("inForce").GetBoolean().ShouldBeTrue();
        (await owner.GetAsync($"/admin/models/{Guid.CreateVersion7()}/prices")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Invalid_prices_are_400_and_two_prices_at_the_same_instant_are_409()
    {
        using var owner = await fixture.OwnerAsync();
        var modelId = await CreateModelAsync(owner, await CreateProviderAsync(owner), "bad-price");

        var bad = await ErrorsAsync(await owner.SendJsonAsync(HttpMethod.Post, $"/admin/models/{modelId}/prices", new { inputPerMillion = -1, cachedInputPerMillion = 0.00000000001, currency = "EUR", effectiveFrom = DateTimeOffset.UtcNow.AddDays(-1) }));
        var missing = await ErrorsAsync(await owner.SendJsonAsync(HttpMethod.Post, $"/admin/models/{modelId}/prices", new { }));
        var at = DateTimeOffset.UtcNow.AddDays(5);
        (await owner.SendJsonAsync(HttpMethod.Post, $"/admin/models/{modelId}/prices", new { inputPerMillion = 1, outputPerMillion = 1, effectiveFrom = at })).StatusCode.ShouldBe(HttpStatusCode.Created);
        var duplicate = await owner.SendJsonAsync(HttpMethod.Post, $"/admin/models/{modelId}/prices", new { inputPerMillion = 2, outputPerMillion = 2, effectiveFrom = at });

        foreach (var field in new[] { "inputPerMillion", "cachedInputPerMillion", "outputPerMillion", "currency", "effectiveFrom" })
        {
            bad.TryGetProperty(field, out _).ShouldBeTrue(field);
        }

        missing.TryGetProperty("outputPerMillion", out _).ShouldBeTrue();
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await owner.SendJsonAsync(HttpMethod.Post, $"/admin/models/{Guid.CreateVersion7()}/prices", new { inputPerMillion = 1, outputPerMillion = 1 })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- Routing Policies --------------------------------------------------------------------------------------

    [Fact]
    [HappyPath("POST /admin/routing-policies")]
    public async Task Creating_a_policy_stores_version_1_with_its_candidates_resolved_and_audits_it()
    {
        using var owner = await fixture.OwnerAsync();
        var (provider, modelId) = await PricedModelAsync(owner);
        var name = Unique("policy");

        var response = await owner.SendJsonAsync(HttpMethod.Post, "/admin/routing-policies", new { name, definition = StaticPolicy($"{provider}/mock-echo") });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await AdminBrowser.JsonAsync(response);
        body.GetProperty("version").GetInt32().ShouldBe(1);
        body.GetProperty("resolvedCandidates").GetProperty($"{provider}/mock-echo").GetGuid().ShouldBe(modelId);
        var policyId = body.GetProperty("policyId").GetGuid();
        (await fixture.ScalarAsync($"select count(*) from identity.audit_record where action = 'routing_policy.created' and target_id = '{policyId}'")).ShouldBe("1");
        (await owner.SendJsonAsync(HttpMethod.Post, "/admin/routing-policies", new { name, definition = StaticPolicy($"{provider}/mock-echo") })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    [HappyPath("PUT /admin/routing-policies/{policyId:guid}")]
    public async Task Saving_a_policy_creates_the_next_version_and_a_stale_expected_version_is_409()
    {
        using var owner = await fixture.OwnerAsync();
        var (provider, _) = await PricedModelAsync(owner);
        var policyId = await CreatePolicyAsync(owner, Unique("pv"), $"{provider}/mock-echo");

        var next = await owner.SendJsonAsync(HttpMethod.Put, $"/admin/routing-policies/{policyId}", new { definition = StaticPolicy($"{provider}/mock-echo"), expectedVersion = 1 });
        var stale = await owner.SendJsonAsync(HttpMethod.Put, $"/admin/routing-policies/{policyId}", new { definition = StaticPolicy($"{provider}/mock-echo"), expectedVersion = 1 });

        next.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AdminBrowser.JsonAsync(next)).GetProperty("version").GetInt32().ShouldBe(2);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await fixture.ScalarAsync($"select latest_version from routing.routing_policy where id = '{policyId}'")).ShouldBe("2");
        (await fixture.ScalarAsync($"select count(*) from routing.routing_policy_version where policy_id = '{policyId}'")).ShouldBe("2");
        (await fixture.ScalarAsync($"select count(*) from identity.audit_record where action = 'routing_policy.versioned' and target_id = '{policyId}'")).ShouldBe("1");
        (await owner.SendJsonAsync(HttpMethod.Put, $"/admin/routing-policies/{Guid.CreateVersion7()}", new { definition = StaticPolicy($"{provider}/mock-echo"), expectedVersion = 1 })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [HappyPath("GET /admin/routing-policies")]
    public async Task The_policy_list_shows_each_policys_latest_version_and_its_aliases()
    {
        using var owner = await fixture.OwnerAsync();
        var (provider, _) = await PricedModelAsync(owner);
        var policyId = await CreatePolicyAsync(owner, Unique("listed"), $"{provider}/mock-echo");
        var alias = Unique("al");
        (await owner.SendJsonAsync(HttpMethod.Put, $"/admin/aliases/{alias}", new { policyId })).EnsureSuccessStatusCode();
        (await owner.SendJsonAsync(HttpMethod.Put, $"/admin/routing-policies/{policyId}", new { definition = StaticPolicy($"{provider}/mock-echo"), expectedVersion = 1 })).EnsureSuccessStatusCode();

        var list = await AdminBrowser.JsonAsync(await owner.GetAsync("/admin/routing-policies?limit=200"));

        var item = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == policyId);
        item.GetProperty("version").GetInt32().ShouldBe(2);
        item.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()).ShouldBe([alias]);
        (await owner.GetAsync("/admin/routing-policies?limit=0")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    [HappyPath("GET /admin/routing-policies/{policyId:guid}/versions")]
    public async Task Policy_history_lists_versions_newest_first()
    {
        using var owner = await fixture.OwnerAsync();
        var (provider, _) = await PricedModelAsync(owner);
        var policyId = await CreatePolicyAsync(owner, Unique("hist"), $"{provider}/mock-echo");
        (await owner.SendJsonAsync(HttpMethod.Put, $"/admin/routing-policies/{policyId}", new { definition = StaticPolicy($"{provider}/mock-echo"), expectedVersion = 1 })).EnsureSuccessStatusCode();

        var page = await AdminBrowser.JsonAsync(await owner.GetAsync($"/admin/routing-policies/{policyId}/versions?limit=1"));
        var rest = await AdminBrowser.JsonAsync(await owner.GetAsync($"/admin/routing-policies/{policyId}/versions?limit=1&cursor={Uri.EscapeDataString(page.GetProperty("nextCursor").GetString()!)}"));

        page.GetProperty("items")[0].GetProperty("version").GetInt32().ShouldBe(2);
        rest.GetProperty("items")[0].GetProperty("version").GetInt32().ShouldBe(1);
        rest.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        (await owner.GetAsync($"/admin/routing-policies/{Guid.CreateVersion7()}/versions")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [HappyPath("GET /admin/routing-policies/{policyId:guid}/versions/{version:int}")]
    public async Task One_policy_version_reads_back_unchanged()
    {
        using var owner = await fixture.OwnerAsync();
        var (provider, _) = await PricedModelAsync(owner);
        var policyId = await CreatePolicyAsync(owner, Unique("one"), $"{provider}/mock-echo");

        var found = await owner.GetAsync($"/admin/routing-policies/{policyId}/versions/1");

        found.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AdminBrowser.JsonAsync(found)).GetProperty("definition").GetProperty("strategy").GetString().ShouldBe("static");
        (await owner.GetAsync($"/admin/routing-policies/{policyId}/versions/9")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [HappyPath("POST /admin/routing-policies/validate")]
    public async Task A_dry_run_validates_without_writing_anything()
    {
        using var owner = await fixture.OwnerAsync();
        var (provider, modelId) = await PricedModelAsync(owner);
        var policiesBefore = await fixture.ScalarAsync("select count(*) from routing.routing_policy");
        var versionBefore = await fixture.ScalarAsync("select version from config.config_version");

        var response = await owner.SendJsonAsync(HttpMethod.Post, "/admin/routing-policies/validate", new { definition = StaticPolicy($"{provider}/mock-echo") });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await AdminBrowser.JsonAsync(response);
        body.GetProperty("valid").GetBoolean().ShouldBeTrue();
        body.GetProperty("resolvedCandidates").GetProperty($"{provider}/mock-echo").GetGuid().ShouldBe(modelId);
        (await fixture.ScalarAsync("select count(*) from routing.routing_policy")).ShouldBe(policiesBefore);
        (await fixture.ScalarAsync("select version from config.config_version")).ShouldBe(versionBefore);
    }

    [Fact]
    public async Task Invalid_policy_bodies_are_400_validation_problems_keyed_by_definition_path()
    {
        using var owner = await fixture.OwnerAsync();
        var (provider, _) = await PricedModelAsync(owner);
        var policyId = await CreatePolicyAsync(owner, Unique("inv"), $"{provider}/mock-echo");

        var unknownModel = await ErrorsAsync(await owner.SendJsonAsync(HttpMethod.Post, "/admin/routing-policies", new { name = "Bad Name", definition = StaticPolicy("nope/nothing") }));
        var noDefinition = await ErrorsAsync(await owner.SendJsonAsync(HttpMethod.Post, "/admin/routing-policies", new { name = Unique("x") }));
        var putNoVersion = await ErrorsAsync(await owner.SendJsonAsync(HttpMethod.Put, $"/admin/routing-policies/{policyId}", new { definition = StaticPolicy($"{provider}/mock-echo") }));
        var validate = await ErrorsAsync(await owner.SendJsonAsync(HttpMethod.Post, "/admin/routing-policies/validate", new { definition = new { strategy = "static", candidates = Array.Empty<string>(), bogus = 1 } }));

        unknownModel.TryGetProperty("name", out _).ShouldBeTrue();
        unknownModel.EnumerateObject().Any(p => p.Name.StartsWith("definition", StringComparison.Ordinal)).ShouldBeTrue();
        noDefinition.TryGetProperty("definition", out _).ShouldBeTrue();
        putNoVersion.TryGetProperty("expectedVersion", out _).ShouldBeTrue();
        validate.EnumerateObject().Count().ShouldBeGreaterThanOrEqualTo(2);
        (await fixture.ScalarAsync($"select count(*) from routing.routing_policy_version where policy_id = '{policyId}'")).ShouldBe("1");
    }

    // ---- Aliases -----------------------------------------------------------------------------------------------

    [Fact]
    [HappyPath("PUT /admin/aliases/{alias}")]
    public async Task Binding_an_alias_creates_then_rebinds_it_and_both_are_audited()
    {
        using var owner = await fixture.OwnerAsync();
        var (provider, _) = await PricedModelAsync(owner);
        var first = await CreatePolicyAsync(owner, Unique("a1"), $"{provider}/mock-echo");
        var second = await CreatePolicyAsync(owner, Unique("a2"), $"{provider}/mock-echo");
        var alias = Unique("bind");

        var created = await owner.SendJsonAsync(HttpMethod.Put, $"/admin/aliases/{alias}", new { policyId = first, description = "fast" });
        var rebound = await owner.SendJsonAsync(HttpMethod.Put, $"/admin/aliases/{alias}", new { policyId = second });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        rebound.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AdminBrowser.JsonAsync(rebound)).GetProperty("policyId").GetGuid().ShouldBe(second);
        (await fixture.ScalarAsync($"select count(*) from identity.audit_record where action = 'model_alias.bound' and target_id = '{alias}'")).ShouldBe("2");
    }

    [Fact]
    [HappyPath("GET /admin/aliases/{alias}")]
    public async Task An_alias_reads_back_with_its_policy_name()
    {
        using var owner = await fixture.OwnerAsync();
        var (provider, _) = await PricedModelAsync(owner);
        var policyName = Unique("an");
        var policyId = await CreatePolicyAsync(owner, policyName, $"{provider}/mock-echo");
        var alias = Unique("get");
        (await owner.SendJsonAsync(HttpMethod.Put, $"/admin/aliases/{alias}", new { policyId })).EnsureSuccessStatusCode();

        var found = await owner.GetAsync($"/admin/aliases/{alias}");

        found.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AdminBrowser.JsonAsync(found)).GetProperty("policyName").GetString().ShouldBe(policyName);
        (await owner.GetAsync("/admin/aliases/does-not-exist")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [HappyPath("DELETE /admin/aliases/{alias}")]
    public async Task Deleting_an_alias_unbinds_it_and_is_audited()
    {
        using var owner = await fixture.OwnerAsync();
        var (provider, _) = await PricedModelAsync(owner);
        var policyId = await CreatePolicyAsync(owner, Unique("del"), $"{provider}/mock-echo");
        var alias = Unique("gone");
        (await owner.SendJsonAsync(HttpMethod.Put, $"/admin/aliases/{alias}", new { policyId })).EnsureSuccessStatusCode();

        var deleted = await owner.SendJsonAsync(HttpMethod.Delete, $"/admin/aliases/{alias}");
        var again = await owner.SendJsonAsync(HttpMethod.Delete, $"/admin/aliases/{alias}");

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        again.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await fixture.ScalarAsync($"select count(*) from identity.audit_record where action = 'model_alias.unbound' and target_id = '{alias}'")).ShouldBe("1");
    }

    [Fact]
    public async Task Invalid_alias_bindings_are_400_validation_problems()
    {
        using var owner = await fixture.OwnerAsync();

        var badName = await ErrorsAsync(await owner.SendJsonAsync(HttpMethod.Put, "/admin/aliases/Bad_Alias!", new { policyId = Guid.CreateVersion7() }));
        var noPolicy = await ErrorsAsync(await owner.SendJsonAsync(HttpMethod.Put, "/admin/aliases/fine", new { }));
        var unknownPolicy = await ErrorsAsync(await owner.SendJsonAsync(HttpMethod.Put, "/admin/aliases/fine", new { policyId = Guid.CreateVersion7() }));

        badName.TryGetProperty("alias", out _).ShouldBeTrue();
        noPolicy.TryGetProperty("policyId", out _).ShouldBeTrue();
        unknownPolicy.TryGetProperty("policyId", out _).ShouldBeTrue();
    }

    // ---- Roles -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_viewer_reads_the_catalog_but_every_write_is_403_and_changes_nothing()
    {
        using var owner = await fixture.OwnerAsync();
        var (provider, modelId) = await PricedModelAsync(owner);
        var providerId = Guid.Parse(await fixture.ScalarAsync($"select id from registry.provider where name = '{provider}'"));
        var policyId = await CreatePolicyAsync(owner, Unique("vw"), $"{provider}/mock-echo");
        using var viewer = await fixture.AdministratorAsync("viewer");
        var version = await fixture.ScalarAsync("select version from config.config_version");
        var def = StaticPolicy($"{provider}/mock-echo");

        foreach (var path in new[] { "/admin/providers", $"/admin/providers/{providerId}", $"/admin/providers/{providerId}/models", $"/admin/models/{modelId}/prices", "/admin/routing-policies", $"/admin/routing-policies/{policyId}/versions", $"/admin/routing-policies/{policyId}/versions/1" })
        {
            (await viewer.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.OK, path);
        }

        var writes = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Post, "/admin/providers", new { name = "x", adapterType = "ollama", endpoint = "https://example.com" }),
            (HttpMethod.Patch, $"/admin/providers/{providerId}", new { enabled = false }),
            (HttpMethod.Post, $"/admin/providers/{providerId}/connectivity-check", null),
            (HttpMethod.Post, $"/admin/providers/{providerId}/models", new { providerModelId = "z", capabilities = Capabilities() }),
            (HttpMethod.Patch, $"/admin/models/{modelId}", new { enabled = false }),
            (HttpMethod.Post, $"/admin/models/{modelId}/prices", new { inputPerMillion = 1, outputPerMillion = 1 }),
            (HttpMethod.Post, "/admin/routing-policies", new { name = "vp", definition = def }),
            (HttpMethod.Post, "/admin/routing-policies/validate", new { definition = def }),
            (HttpMethod.Put, $"/admin/routing-policies/{policyId}", new { definition = def, expectedVersion = 1 }),
            (HttpMethod.Put, "/admin/aliases/viewer-alias", new { policyId }),
            (HttpMethod.Delete, "/admin/aliases/viewer-alias", null),
        };
        foreach (var (method, path, body) in writes)
        {
            var response = await viewer.SendJsonAsync(method, path, body);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{method} {path}");
            (await AdminBrowser.ErrorCodeAsync(response)).ShouldBe("forbidden");
        }

        (await fixture.ScalarAsync("select version from config.config_version")).ShouldBe(version);
    }

    // ---- Cross-cutting scenarios ---------------------------------------------------------------------------------

    /// <summary>
    /// Waits until host B serves the configuration version the database holds now, failing after <paramref name="within"/>.
    /// A fixed sleep raced the NOTIFY under full-suite load.
    /// </summary>
    private async Task HostBCaughtUpAsync(TimeSpan within)
    {
        var target = long.Parse(await fixture.ScalarAsync("select version from config.config_version"), System.Globalization.CultureInfo.InvariantCulture);
        var snapshots = fixture.ServicesB.GetRequiredService<IConfigurationSnapshotProvider>();
        await Eventually.UntilAsync(() => snapshots.IsLoaded && snapshots.Current.Version >= target, within, $"host B serves config version {target}");
    }

    private async Task<(string RequestId, string ProviderModelId)> ChatOnHostBAsync(string alias)
    {
        using var client = fixture.InferenceClientB();
        var body = new StringContent($$"""{"model":"{{alias}}","messages":[{"role":"user","content":"hello there"}]}""", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/chat/completions", body, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var requestId = response.Headers.GetValues("X-Request-Id").Single();
        var modelId = await fixture.ScalarAsync($"select provider_model_id from usage.provider_attempt where inference_request_id = '{requestId}'");
        return (requestId, modelId);
    }

    [Fact]
    public async Task Adding_a_price_through_the_admin_api_leaves_recorded_usage_costs_unchanged()
    {
        using var owner = await fixture.OwnerAsync();
        var (provider, modelId) = await PricedModelAsync(owner);
        var policyId = await CreatePolicyAsync(owner, Unique("pp"), $"{provider}/mock-echo");
        var alias = Unique("price");
        (await owner.SendJsonAsync(HttpMethod.Put, $"/admin/aliases/{alias}", new { policyId })).EnsureSuccessStatusCode();
        await HostBCaughtUpAsync(TimeSpan.FromSeconds(10));
        var (firstRequest, _) = await ChatOnHostBAsync(alias);
        var costBefore = await fixture.ScalarAsync($"select total_cost::text || '/' || model_price_id::text from usage.usage_record where inference_request_id = '{firstRequest}'");

        await AddPriceAsync(owner, modelId, input: 100, output: 200);
        await HostBCaughtUpAsync(TimeSpan.FromSeconds(10));
        var (secondRequest, _) = await ChatOnHostBAsync(alias);

        (await fixture.ScalarAsync($"select total_cost::text || '/' || model_price_id::text from usage.usage_record where inference_request_id = '{firstRequest}'")).ShouldBe(costBefore);
        var firstCost = decimal.Parse(costBefore.Split('/')[0], System.Globalization.CultureInfo.InvariantCulture);
        var secondCost = decimal.Parse(await fixture.ScalarAsync($"select total_cost from usage.usage_record where inference_request_id = '{secondRequest}'"), System.Globalization.CultureInfo.InvariantCulture);
        secondCost.ShouldBeGreaterThan(firstCost * 10);
        (await fixture.ScalarAsync($"select count(distinct model_price_id) from usage.usage_record where inference_request_id in ('{firstRequest}', '{secondRequest}')")).ShouldBe("2");
        (await fixture.ScalarAsync($"select count(*) from registry.model_price where provider_model_id = '{modelId}'")).ShouldBe("2");
    }

    [Fact]
    public async Task A_policy_updated_on_host_A_is_used_by_host_B_within_one_second()
    {
        using var owner = await fixture.OwnerAsync();
        var (provider, firstModel) = await PricedModelAsync(owner, "mock-echo");
        var secondProviderId = Guid.Parse(await fixture.ScalarAsync($"select id from registry.provider where name = '{provider}'"));
        var secondModel = await CreateModelAsync(owner, secondProviderId, "mock-fallback");
        await AddPriceAsync(owner, secondModel);
        var policyId = await CreatePolicyAsync(owner, Unique("prop"), $"{provider}/mock-echo");
        var alias = Unique("prop");
        (await owner.SendJsonAsync(HttpMethod.Put, $"/admin/aliases/{alias}", new { policyId })).EnsureSuccessStatusCode();
        await HostBCaughtUpAsync(TimeSpan.FromSeconds(10));
        (await ChatOnHostBAsync(alias)).ProviderModelId.ShouldBe(firstModel.ToString());

        var update = await owner.SendJsonAsync(HttpMethod.Put, $"/admin/routing-policies/{policyId}", new { definition = StaticPolicy($"{provider}/mock-fallback"), expectedVersion = 1 });
        update.EnsureSuccessStatusCode();
        await HostBCaughtUpAsync(TimeSpan.FromSeconds(1));

        var (requestId, used) = await ChatOnHostBAsync(alias);
        used.ShouldBe(secondModel.ToString());
        (await fixture.ScalarAsync($"select policy_version from usage.inference_request where id = '{requestId}'")).ShouldBe("2");
    }
}
