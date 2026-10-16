using System.Net;
using System.Text;
using System.Text.Json;
using ModelSwitch.IntegrationTests.Infrastructure;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>Datasets, Dataset Versions and Prompt Versions (api.md section 3, Evaluation; evaluation.md sections 1 and 2).</summary>
public sealed class EvaluationEndpointTests(AdminFixture admin) : IClassFixture<AdminFixture>
{
    private const string _twoCases = """
        {"id":"c-1","input":{"text":"Buy now!"},"expected":"spam","metadata":{"lang":"en"}}
        {"id":"c-2","input":{"text":"Lunch?"},"expected":"ham"}

        """;

    private async Task<int> AuditCountAsync(string action, Guid targetId)
        => int.Parse(await admin.ScalarAsync($"select count(*) from identity.audit_record where action = '{action}' and target_id = '{targetId}'"), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<Guid> CreateDatasetAsync(AdminBrowser browser, Guid tenantId, string taskKind = "classification", string? name = null)
    {
        var response = await browser.SendJsonAsync(HttpMethod.Post, "/admin/datasets", new { tenantId, name = name ?? $"dataset-{Guid.CreateVersion7()}", taskKind });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await AdminBrowser.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> UploadAsync(AdminBrowser browser, Guid datasetId, string jsonl, string contentType = "application/x-ndjson")
        => browser.SendJsonAsync(HttpMethod.Post, $"/admin/datasets/{datasetId}/versions", new StringContent(jsonl, Encoding.UTF8, contentType));

    private static async Task<Guid> CreatePromptAsync(AdminBrowser browser, Guid tenantId)
    {
        var response = await browser.SendJsonAsync(HttpMethod.Post, "/admin/prompts", new { tenantId, name = $"prompt-{Guid.CreateVersion7()}" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await AdminBrowser.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task AssertValidationProblemAsync(HttpResponseMessage response, params string[] fields)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await AdminBrowser.JsonAsync(response);
        body.GetProperty("errorCode").GetString().ShouldBe("validation_failed");
        body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ShouldBe(fields.Order(StringComparer.Ordinal));
    }

    [Fact]
    [HappyPath("POST /admin/datasets")]
    public async Task An_operator_creates_a_dataset_in_a_granted_tenant_and_it_is_audited()
    {
        using var op = await admin.SignedInAsync($"ds-op-{Guid.CreateVersion7()}@example.com", "operator", admin.TenantA);

        var response = await op.SendJsonAsync(HttpMethod.Post, "/admin/datasets", new { tenantId = admin.TenantA, name = "  Support triage  ", taskKind = "classification" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await AdminBrowser.JsonAsync(response);
        var id = body.GetProperty("id").GetGuid();
        response.Headers.Location!.ToString().ShouldBe($"/admin/datasets/{id}");
        body.GetProperty("tenantId").GetGuid().ShouldBe(admin.TenantA);
        body.GetProperty("name").GetString().ShouldBe("Support triage");
        body.GetProperty("taskKind").GetString().ShouldBe("classification");
        body.GetProperty("latestVersion").GetInt32().ShouldBe(0);
        (await AuditCountAsync("dataset.created", id)).ShouldBe(1);
        (await admin.ScalarAsync($"select tenant_id from identity.audit_record where action = 'dataset.created' and target_id = '{id}'")).ShouldBe(admin.TenantA.ToString());
    }

    [Fact]
    public async Task An_invalid_dataset_is_a_400_validation_problem_and_a_duplicate_name_is_409()
    {
        using var owner = await admin.SignedInOwnerAsync();

        await AssertValidationProblemAsync(await owner.SendJsonAsync(HttpMethod.Post, "/admin/datasets", new { name = " ", taskKind = "ranking" }), "tenantId", "name", "taskKind");

        var name = $"dup-{Guid.CreateVersion7()}";
        await CreateDatasetAsync(owner, admin.TenantA, name: name);
        (await owner.SendJsonAsync(HttpMethod.Post, "/admin/datasets", new { tenantId = admin.TenantA, name, taskKind = "free_text" })).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // The same name in another Tenant is fine, and an unknown Tenant is 404 even for an Owner.
        await CreateDatasetAsync(owner, admin.TenantB, name: name);
        (await owner.SendJsonAsync(HttpMethod.Post, "/admin/datasets", new { tenantId = Guid.CreateVersion7(), name, taskKind = "free_text" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_viewer_cannot_create_datasets_or_prompts()
    {
        using var viewer = await admin.SignedInAsync($"ds-viewer-{Guid.CreateVersion7()}@example.com", "viewer", admin.TenantA);

        (await viewer.SendJsonAsync(HttpMethod.Post, "/admin/datasets", new { tenantId = admin.TenantA, name = "x", taskKind = "free_text" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.SendJsonAsync(HttpMethod.Post, "/admin/prompts", new { tenantId = admin.TenantA, name = "x" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    [HappyPath("GET /admin/datasets")]
    public async Task Datasets_are_listed_newest_first_within_the_grant_with_cursor_paging()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var tenant = await NewTenantAsync();
        var first = await CreateDatasetAsync(owner, tenant);
        var second = await CreateDatasetAsync(owner, tenant);
        var third = await CreateDatasetAsync(owner, tenant);
        var foreign = await CreateDatasetAsync(owner, admin.TenantB);
        using var viewer = await admin.SignedInAsync($"ds-list-{Guid.CreateVersion7()}@example.com", "viewer", tenant);

        var page1 = await AdminBrowser.JsonAsync(await viewer.GetAsync("/admin/datasets?limit=2"));
        var page2 = await AdminBrowser.JsonAsync(await viewer.GetAsync($"/admin/datasets?limit=2&cursor={page1.GetProperty("nextCursor").GetString()}"));
        var filtered = await AdminBrowser.JsonAsync(await owner.GetAsync($"/admin/datasets?tenantId={tenant}"));

        page1.GetProperty("items").EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ShouldBe([third, second]);
        page2.GetProperty("items").EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ShouldBe([first]);
        page2.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        filtered.GetProperty("items").EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ShouldNotContain(foreign);
        await AssertValidationProblemAsync(await viewer.GetAsync("/admin/datasets?tenantId=nope&limit=0&cursor=***"), "tenantId", "limit", "cursor");
    }

    [Fact]
    [HappyPath("GET /admin/datasets/{datasetId:guid}")]
    public async Task A_dataset_is_read_by_id_and_an_unknown_one_is_404()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var id = await CreateDatasetAsync(owner, admin.TenantA, "extraction");

        var body = await AdminBrowser.JsonAsync(await owner.GetAsync($"/admin/datasets/{id}"));

        body.GetProperty("taskKind").GetString().ShouldBe("extraction");
        (await owner.GetAsync($"/admin/datasets/{Guid.CreateVersion7()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [HappyPath("POST /admin/datasets/{datasetId:guid}/versions")]
    public async Task Uploading_jsonl_creates_an_immutable_version_with_hash_scoring_schema_and_audit()
    {
        using var op = await admin.SignedInAsync($"ds-up-{Guid.CreateVersion7()}@example.com", "operator", admin.TenantA);
        var id = await CreateDatasetAsync(op, admin.TenantA);

        var response = await UploadAsync(op, id, _twoCases);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.ToString().ShouldBe($"/admin/datasets/{id}/versions/1");
        var body = await AdminBrowser.JsonAsync(response);
        body.GetProperty("datasetId").GetGuid().ShouldBe(id);
        body.GetProperty("version").GetInt32().ShouldBe(1);
        body.GetProperty("caseCount").GetInt32().ShouldBe(2);
        body.GetProperty("contentSha256").GetString()!.ShouldMatch("^[0-9a-f]{64}$");
        body.GetProperty("scoringSchema").GetProperty("labels").EnumerateArray().Select(l => l.GetString()).ShouldBe(["ham", "spam"]);
        body.GetProperty("createdByAdminId").ValueKind.ShouldBe(JsonValueKind.String);
        (await admin.ScalarAsync($"select latest_version from evaluation.dataset where id = '{id}'")).ShouldBe("1");
        (await admin.ScalarAsync($"select count(*) from evaluation.eval_case where dataset_id = '{id}' and dataset_version = 1")).ShouldBe("2");
        (await AuditCountAsync("dataset.versioned", id)).ShouldBe(1);

        // The audit summary carries the hash and counts, never Case content.
        var details = await admin.ScalarAsync($"select details::text from identity.audit_record where action = 'dataset.versioned' and target_id = '{id}'");
        details.ShouldNotContain("Buy now");

        // The same content uploaded again is a new version with the same hash.
        var again = await AdminBrowser.JsonAsync(await UploadAsync(op, id, _twoCases.Replace("\n", "\r\n", StringComparison.Ordinal), "application/jsonl"));
        again.GetProperty("version").GetInt32().ShouldBe(2);
        again.GetProperty("contentSha256").GetString().ShouldBe(body.GetProperty("contentSha256").GetString());
    }

    [Fact]
    public async Task An_invalid_upload_is_a_400_with_line_numbered_errors_and_writes_nothing()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var id = await CreateDatasetAsync(owner, admin.TenantA);

        var response = await UploadAsync(owner, id, "{\"id\":\"a\",\"input\":{},\"expected\":\"x\"}\n{\"id\":\"a\",\"input\":{},\"expected\":{}}\nnot json\n");

        await AssertValidationProblemAsync(response, "lines[2].id", "lines[2].expected", "lines[3]");
        (await admin.ScalarAsync($"select latest_version from evaluation.dataset where id = '{id}'")).ShouldBe("0");
        (await admin.ScalarAsync($"select count(*) from evaluation.eval_case where dataset_id = '{id}'")).ShouldBe("0");
        await AssertValidationProblemAsync(await UploadAsync(owner, id, "\n"), "body");
    }

    [Fact]
    public async Task An_upload_with_another_media_type_is_415_and_one_over_10_MiB_is_413()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var id = await CreateDatasetAsync(owner, admin.TenantA);

        var wrongType = await UploadAsync(owner, id, _twoCases, "application/json");
        wrongType.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        (await AdminBrowser.ErrorCodeAsync(wrongType)).ShouldBe("unsupported_media_type");

        var tooLarge = await UploadAsync(owner, id, new string(' ', (10 * 1024 * 1024) + 1));
        tooLarge.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await AdminBrowser.ErrorCodeAsync(tooLarge)).ShouldBe("payload_too_large");
        (await admin.ScalarAsync($"select latest_version from evaluation.dataset where id = '{id}'")).ShouldBe("0");
    }

    [Fact]
    public async Task Concurrent_uploads_each_get_their_own_version_number()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var id = await CreateDatasetAsync(owner, admin.TenantA);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => UploadAsync(owner, id, _twoCases)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Created);
        var versions = await Task.WhenAll(responses.Select(async r => (await AdminBrowser.JsonAsync(r)).GetProperty("version").GetInt32()));
        versions.Order().ShouldBe([1, 2, 3, 4, 5, 6]);
        (await admin.ScalarAsync($"select latest_version from evaluation.dataset where id = '{id}'")).ShouldBe("6");
    }

    [Fact]
    [HappyPath("GET /admin/datasets/{datasetId:guid}/versions")]
    [HappyPath("GET /admin/datasets/{datasetId:guid}/versions/{version:int}")]
    public async Task Versions_are_listed_newest_first_and_read_one_by_one()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var id = await CreateDatasetAsync(owner, admin.TenantA);
        await UploadAsync(owner, id, _twoCases);
        await UploadAsync(owner, id, "{\"id\":\"only\",\"input\":{},\"expected\":\"ham\"}");
        using var viewer = await admin.SignedInAsync($"ds-v-{Guid.CreateVersion7()}@example.com", "viewer", admin.TenantA);

        var page1 = await AdminBrowser.JsonAsync(await viewer.GetAsync($"/admin/datasets/{id}/versions?limit=1"));
        var page2 = await AdminBrowser.JsonAsync(await viewer.GetAsync($"/admin/datasets/{id}/versions?limit=1&cursor={page1.GetProperty("nextCursor").GetString()}"));
        var one = await AdminBrowser.JsonAsync(await viewer.GetAsync($"/admin/datasets/{id}/versions/1"));

        page1.GetProperty("items").EnumerateArray().Select(v => v.GetProperty("version").GetInt32()).ShouldBe([2]);
        page2.GetProperty("items").EnumerateArray().Select(v => v.GetProperty("version").GetInt32()).ShouldBe([1]);
        one.GetProperty("caseCount").GetInt32().ShouldBe(2);
        (await viewer.GetAsync($"/admin/datasets/{id}/versions/3")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [HappyPath("GET /admin/datasets/{datasetId:guid}/versions/{version:int}/cases")]
    public async Task Cases_are_paged_in_upload_order_with_their_values()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var id = await CreateDatasetAsync(owner, admin.TenantA);
        await UploadAsync(owner, id, _twoCases);

        var page1 = await AdminBrowser.JsonAsync(await owner.GetAsync($"/admin/datasets/{id}/versions/1/cases?limit=1"));
        var page2 = await AdminBrowser.JsonAsync(await owner.GetAsync($"/admin/datasets/{id}/versions/1/cases?limit=1&cursor={page1.GetProperty("nextCursor").GetString()}"));

        var first = page1.GetProperty("items").EnumerateArray().Single();
        first.GetProperty("externalId").GetString().ShouldBe("c-1");
        first.GetProperty("ordinal").GetInt32().ShouldBe(0);
        first.GetProperty("input").GetProperty("text").GetString().ShouldBe("Buy now!");
        first.GetProperty("expected").GetString().ShouldBe("spam");
        first.GetProperty("metadata").GetProperty("lang").GetString().ShouldBe("en");
        var second = page2.GetProperty("items").EnumerateArray().Single();
        second.GetProperty("externalId").GetString().ShouldBe("c-2");
        second.GetProperty("metadata").ValueKind.ShouldBe(JsonValueKind.Null);
        page2.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        (await owner.GetAsync($"/admin/datasets/{id}/versions/2/cases")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [HappyPath("POST /admin/prompts")]
    [HappyPath("GET /admin/prompts/{promptId:guid}")]
    public async Task An_operator_creates_a_prompt_that_is_audited_and_readable()
    {
        using var op = await admin.SignedInAsync($"pr-op-{Guid.CreateVersion7()}@example.com", "operator", admin.TenantA);

        var response = await op.SendJsonAsync(HttpMethod.Post, "/admin/prompts", new { tenantId = admin.TenantA, name = "extract-contact" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await AdminBrowser.JsonAsync(response)).GetProperty("id").GetGuid();
        response.Headers.Location!.ToString().ShouldBe($"/admin/prompts/{id}");
        var read = await AdminBrowser.JsonAsync(await op.GetAsync($"/admin/prompts/{id}"));
        read.GetProperty("name").GetString().ShouldBe("extract-contact");
        read.GetProperty("latestVersion").GetInt32().ShouldBe(0);
        (await AuditCountAsync("prompt.created", id)).ShouldBe(1);
        (await op.SendJsonAsync(HttpMethod.Post, "/admin/prompts", new { tenantId = admin.TenantA, name = "extract-contact" })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await AssertValidationProblemAsync(await op.SendJsonAsync(HttpMethod.Post, "/admin/prompts", new { name = "" }), "tenantId", "name");
    }

    [Fact]
    [HappyPath("GET /admin/prompts")]
    public async Task Prompts_are_listed_within_the_grant()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var tenant = await NewTenantAsync();
        var mine = await CreatePromptAsync(owner, tenant);
        var foreign = await CreatePromptAsync(owner, admin.TenantB);
        using var viewer = await admin.SignedInAsync($"pr-list-{Guid.CreateVersion7()}@example.com", "viewer", tenant);

        var body = await AdminBrowser.JsonAsync(await viewer.GetAsync("/admin/prompts"));

        body.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ShouldBe([mine]);
        body.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ShouldNotContain(foreign);
    }

    [Fact]
    [HappyPath("POST /admin/prompts/{promptId:guid}/versions")]
    [HappyPath("GET /admin/prompts/{promptId:guid}/versions")]
    [HappyPath("GET /admin/prompts/{promptId:guid}/versions/{version:int}")]
    public async Task Prompt_versions_are_appended_immutable_and_read_back()
    {
        using var op = await admin.SignedInAsync($"pr-v-{Guid.CreateVersion7()}@example.com", "operator", admin.TenantA);
        var id = await CreatePromptAsync(op, admin.TenantA);
        var template = new
        {
            messages = new[]
            {
                new { role = "system", content = "Extract the customer's name and email. Reply with JSON." },
                new { role = "user", content = "{{text}}" },
            },
            responseFormat = new { type = "json_schema", schema = new { type = "object" } },
        };

        var created = await op.SendJsonAsync(HttpMethod.Post, $"/admin/prompts/{id}/versions", template);
        var second = await op.SendJsonAsync(HttpMethod.Post, $"/admin/prompts/{id}/versions", new { messages = new[] { new { role = "user", content = "Classify: {{text}}" } } });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        created.Headers.Location!.ToString().ShouldBe($"/admin/prompts/{id}/versions/1");
        (await AdminBrowser.JsonAsync(second)).GetProperty("version").GetInt32().ShouldBe(2);
        var v1 = await AdminBrowser.JsonAsync(await op.GetAsync($"/admin/prompts/{id}/versions/1"));
        v1.GetProperty("template").GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()).ShouldBe(["system", "user"]);
        v1.GetProperty("template").GetProperty("messages")[1].GetProperty("content").GetString().ShouldBe("{{text}}");
        v1.GetProperty("template").GetProperty("responseFormat").GetProperty("type").GetString().ShouldBe("json_schema");
        var list = await AdminBrowser.JsonAsync(await op.GetAsync($"/admin/prompts/{id}/versions"));
        list.GetProperty("items").EnumerateArray().Select(v => v.GetProperty("version").GetInt32()).ShouldBe([2, 1]);
        list.GetProperty("items")[0].GetProperty("template").GetProperty("responseFormat").ValueKind.ShouldBe(JsonValueKind.Null);
        (await op.GetAsync($"/admin/prompts/{id}/versions/9")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AuditCountAsync("prompt.versioned", id)).ShouldBe(2);
        (await admin.ScalarAsync($"select details::text from identity.audit_record where action = 'prompt.versioned' and target_id = '{id}' limit 1")).ShouldNotContain("Extract");
    }

    [Fact]
    public async Task An_invalid_prompt_template_is_a_400_validation_problem_and_writes_nothing()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var id = await CreatePromptAsync(owner, admin.TenantA);

        var response = await owner.SendJsonAsync(
            HttpMethod.Post,
            $"/admin/prompts/{id}/versions",
            new { messages = new[] { new { role = "tool", content = "x" }, new { role = "user", content = " " } }, responseFormat = new { type = "yaml" } });

        await AssertValidationProblemAsync(response, "messages[0].role", "messages[1].content", "responseFormat.type");
        await AssertValidationProblemAsync(await owner.SendJsonAsync(HttpMethod.Post, $"/admin/prompts/{id}/versions", new { }), "messages");
        (await admin.ScalarAsync($"select latest_version from evaluation.prompt where id = '{id}'")).ShouldBe("0");
    }

    private async Task<Guid> NewTenantAsync()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var response = await owner.SendJsonAsync(HttpMethod.Post, "/admin/tenants", new { name = $"eval-{Guid.CreateVersion7()}" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await AdminBrowser.JsonAsync(response)).GetProperty("id").GetGuid();
    }
}
