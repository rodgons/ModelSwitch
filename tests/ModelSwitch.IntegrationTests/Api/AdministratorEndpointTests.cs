using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.IntegrationTests.Infrastructure;
using Npgsql;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>Administrator management and audit records (Owner only) and the audit writer's transaction behaviour.</summary>
public sealed class AdministratorEndpointTests(AdminFixture admin) : IClassFixture<AdminFixture>
{
    private const string _password = "an-ordinary-password-1";

    [Fact]
    [HappyPath("POST /admin/administrators")]
    public async Task An_owner_creates_an_administrator_who_can_then_sign_in()
    {
        using var owner = await admin.SignedInOwnerAsync();

        var response = await owner.SendJsonAsync(
            HttpMethod.Post,
            "/admin/administrators",
            new { email = "New.Operator@Example.com", password = _password, role = "operator", tenantIds = new[] { admin.TenantA } });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await AdminBrowser.JsonAsync(response);
        response.Headers.Location!.ToString().ShouldBe($"/admin/administrators/{body.GetProperty("id").GetGuid()}");
        body.GetProperty("email").GetString().ShouldBe("new.operator@example.com");
        body.GetProperty("role").GetString().ShouldBe("operator");
        body.GetProperty("tenantIds").EnumerateArray().Select(e => e.GetGuid()).ShouldBe([admin.TenantA]);
        body.TryGetProperty("passwordHash", out _).ShouldBeFalse();
        body.ToString().ShouldNotContain(_password);

        using var created = admin.NewBrowser();
        await created.SignInAsync("new.operator@example.com", _password);
        (await created.GetAsync($"{AdminFixture.TestTenantRoute}{admin.TenantA}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_invalid_administrator_is_a_400_validation_problem_listing_each_field()
    {
        using var owner = await admin.SignedInOwnerAsync();

        var response = await owner.SendJsonAsync(HttpMethod.Post, "/admin/administrators", new { email = "nope", password = "short", role = "boss" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await AdminBrowser.JsonAsync(response);
        body.GetProperty("errorCode").GetString().ShouldBe("validation_failed");
        var errors = body.GetProperty("errors");
        errors.TryGetProperty("email", out _).ShouldBeTrue();
        errors.TryGetProperty("password", out _).ShouldBeTrue();
        errors.TryGetProperty("role", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task A_duplicate_email_is_a_409_conflict_and_an_unknown_tenant_a_400()
    {
        await admin.CreateAdministratorAsync("dup@example.com", "viewer");
        using var owner = await admin.SignedInOwnerAsync();

        var duplicate = await owner.SendJsonAsync(HttpMethod.Post, "/admin/administrators", new { email = "DUP@example.com", password = _password, role = "viewer" });
        var unknownTenant = await owner.SendJsonAsync(HttpMethod.Post, "/admin/administrators", new { email = "ghost@example.com", password = _password, role = "viewer", tenantIds = new[] { Guid.CreateVersion7() } });

        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        unknownTenant.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    [HappyPath("GET /admin/administrators")]
    public async Task An_owner_lists_administrators_without_password_material()
    {
        await admin.CreateAdministratorAsync("listed@example.com", "viewer");
        using var owner = await admin.SignedInOwnerAsync();

        var response = await owner.GetAsync("/admin/administrators");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.ShouldContain("listed@example.com");
        text.ShouldContain(AdminFixture.OwnerEmail);
        text.ShouldNotContain("password", Case.Insensitive);
        JsonDocument.Parse(text).RootElement.GetProperty("items").GetArrayLength().ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    [HappyPath("PATCH /admin/administrators/{administratorId:guid}")]
    public async Task An_owner_changes_a_role_and_the_change_is_audited_with_before_and_after()
    {
        var id = await admin.CreateAdministratorAsync("patched@example.com", "viewer", admin.TenantA);
        using var owner = await admin.SignedInOwnerAsync();

        var response = await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/administrators/{id}", new { role = "operator" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AdminBrowser.JsonAsync(response)).GetProperty("role").GetString().ShouldBe("operator");
        var audit = await admin.ScalarAsync($"select details::text from identity.audit_record where action = 'administrator.modified' and target_id = '{id}'");
        var details = JsonDocument.Parse(audit).RootElement;
        details.GetProperty("before").GetProperty("role").GetString().ShouldBe("viewer");
        details.GetProperty("after").GetProperty("role").GetString().ShouldBe("operator");
    }

    [Fact]
    public async Task Patching_an_unknown_administrator_is_404_and_demoting_the_last_owner_is_409()
    {
        using var owner = await admin.SignedInOwnerAsync();
        var ownerId = (await AdminBrowser.JsonAsync(await owner.GetAsync("/admin/session"))).GetProperty("administrator").GetProperty("id").GetGuid();

        var unknown = await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/administrators/{Guid.CreateVersion7()}", new { role = "viewer" });
        var lastOwner = await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/administrators/{ownerId}", new { active = false });

        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        lastOwner.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    [HappyPath("GET /admin/audit-records")]
    public async Task Audit_records_list_newest_first_filter_by_action_and_page_by_cursor()
    {
        var email = $"audited-{Guid.CreateVersion7():N}@example.com";
        var id = await admin.CreateAdministratorAsync(email, "viewer");
        using var owner = await admin.SignedInOwnerAsync();
        await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/administrators/{id}", new { role = "operator" });

        var filtered = await owner.GetAsync("/admin/audit-records?action=administrator.created&limit=1");
        filtered.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await AdminBrowser.JsonAsync(filtered);
        var first = page.GetProperty("items")[0];
        first.GetProperty("action").GetString().ShouldBe("administrator.created");
        first.GetProperty("targetType").GetString().ShouldBe("administrator");
        first.GetProperty("administratorId").ValueKind.ShouldNotBe(JsonValueKind.Null);
        first.GetProperty("requestId").ValueKind.ShouldNotBe(JsonValueKind.Null);
        first.GetProperty("sourceIp").GetString().ShouldBe(AdminFixture.SourceAddress);
        first.GetProperty("details").GetProperty("after").GetProperty("email").GetString().ShouldNotBeNull();
        page.GetProperty("nextCursor").GetString().ShouldNotBeNullOrEmpty();

        var everything = await AdminBrowser.JsonAsync(await owner.GetAsync("/admin/audit-records?limit=200"));
        var times = everything.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("occurredAt").GetDateTimeOffset()).ToList();
        times.ShouldBe(times.OrderByDescending(t => t).ToList());

        var secondPage = await AdminBrowser.JsonAsync(await owner.GetAsync($"/admin/audit-records?action=administrator.created&limit=1&cursor={Uri.EscapeDataString(page.GetProperty("nextCursor").GetString()!)}"));
        secondPage.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldNotBe(first.GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=9999")]
    [InlineData("tenantId=not-a-guid")]
    [InlineData("from=yesterday-ish")]
    [InlineData("cursor=%%%")]
    public async Task A_bad_audit_query_is_a_400_validation_problem(string query)
    {
        using var owner = await admin.SignedInOwnerAsync();

        var response = await owner.GetAsync($"/admin/audit-records?{query}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AdminBrowser.ErrorCodeAsync(response)).ShouldBe("validation_failed");
    }

    [Fact]
    public async Task An_audit_record_written_on_the_callers_transaction_commits_and_rolls_back_with_it()
    {
        var writer = admin.Services.GetRequiredService<IAuditWriter>();
        var rolledBackId = $"rolled-back-{Guid.CreateVersion7():N}";
        var committedId = $"committed-{Guid.CreateVersion7():N}";

        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var rolledBack = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await writer.WriteAsync(new AuditEntry("provider.modified", "provider", rolledBackId), rolledBack, TestContext.Current.CancellationToken);
            await rolledBack.RollbackAsync(TestContext.Current.CancellationToken);

            await using var committed = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await writer.WriteAsync(new AuditEntry("provider.modified", "provider", committedId) { TenantId = admin.TenantA, After = new Dictionary<string, object?> { ["name"] = "openai", ["apiKey"] = "sk-should-never-land" } }, committed, TestContext.Current.CancellationToken);
            await committed.CommitAsync(TestContext.Current.CancellationToken);
        }

        (await admin.ScalarAsync($"select count(*) from identity.audit_record where target_id = '{rolledBackId}'")).ShouldBe("0");
        (await admin.ScalarAsync($"select count(*) from identity.audit_record where target_id = '{committedId}' and tenant_id = '{admin.TenantA}'")).ShouldBe("1");
        (await admin.ScalarAsync($"select details::text from identity.audit_record where target_id = '{committedId}'")).ShouldNotContain("sk-should-never-land");
    }

    [Fact]
    public async Task The_standalone_audit_overload_writes_in_its_own_transaction_with_no_actor_outside_a_request()
    {
        var writer = admin.Services.GetRequiredService<IAuditWriter>();
        var target = $"standalone-{Guid.CreateVersion7():N}";

        await writer.WriteAsync(new AuditEntry("tenant.suspended", "tenant", target) { TenantId = admin.TenantB }, TestContext.Current.CancellationToken);

        (await admin.ScalarAsync($"select count(*) from identity.audit_record where target_id = '{target}' and administrator_id is null and request_id is null")).ShouldBe("1");
    }
}
