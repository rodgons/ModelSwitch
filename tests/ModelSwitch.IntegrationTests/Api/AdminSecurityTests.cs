using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ModelSwitch.IntegrationTests.Infrastructure;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>The admin security tests of testing.md section 4: Roles, antiforgery and Tenant Grants.</summary>
public sealed class AdminSecurityTests(AdminFixture admin) : IClassFixture<AdminFixture>
{
    private const string _password = "an-ordinary-password-1";

    [Fact]
    public async Task A_missing_antiforgery_token_on_sign_in_is_a_400_validation_problem()
    {
        using var browser = admin.NewBrowser();

        var response = await browser.SendJsonAsync(HttpMethod.Post, "/admin/session", new { email = AdminFixture.OwnerEmail, password = AdminFixture.OwnerPassword }, withToken: false);

        await ShouldBeAntiforgeryProblemAsync(response);
    }

    [Fact]
    public async Task A_missing_antiforgery_token_on_an_unsafe_request_with_a_valid_session_is_a_400()
    {
        using var browser = await admin.SignedInOwnerAsync();

        var response = await browser.SendJsonAsync(
            HttpMethod.Post,
            "/admin/administrators",
            new { email = "csrf@example.com", password = _password, role = "viewer" },
            withToken: false);

        await ShouldBeAntiforgeryProblemAsync(response);
        (await admin.ScalarAsync("select count(*) from identity.administrator where email = 'csrf@example.com'")).ShouldBe("0");
    }

    [Fact]
    public async Task A_garbage_antiforgery_token_is_rejected_and_a_token_from_another_session_does_not_transfer()
    {
        using var owner = await admin.SignedInOwnerAsync();
        using var other = await admin.SignedInOwnerAsync();

        using var forged = new HttpRequestMessage(HttpMethod.Post, "/admin/administrators");
        forged.Headers.Add("X-XSRF-TOKEN", "not-a-token");
        await ShouldBeAntiforgeryProblemAsync(await owner.Client.SendAsync(forged, TestContext.Current.CancellationToken));

        // The token is bound to the cookie that came with it: another browser's token fails with this browser's cookie.
        using var crossed = new HttpRequestMessage(HttpMethod.Post, "/admin/administrators");
        crossed.Headers.Add("X-XSRF-TOKEN", other.AntiforgeryToken);
        await ShouldBeAntiforgeryProblemAsync(await owner.Client.SendAsync(crossed, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Safe_methods_need_no_antiforgery_token()
    {
        using var browser = await admin.SignedInOwnerAsync();

        var response = await browser.SendJsonAsync(HttpMethod.Get, "/admin/administrators", withToken: false);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("operator")]
    public async Task A_viewer_or_operator_posting_to_an_owner_endpoint_is_403_forbidden(string role)
    {
        await admin.CreateAdministratorAsync($"{role}-forbidden@example.com", role, admin.TenantA);
        using var browser = admin.NewBrowser();
        await browser.SignInAsync($"{role}-forbidden@example.com", _password);

        var post = await browser.SendJsonAsync(HttpMethod.Post, "/admin/administrators", new { email = $"x-{role}@example.com", password = _password, role = "viewer" });
        var patch = await browser.SendJsonAsync(HttpMethod.Patch, $"/admin/administrators/{Guid.CreateVersion7()}", new { role = "owner" });
        var read = await browser.GetAsync("/admin/administrators");
        var audit = await browser.GetAsync("/admin/audit-records");

        foreach (var response in new[] { post, patch, read, audit })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
            (await AdminBrowser.ErrorCodeAsync(response)).ShouldBe("forbidden");
        }

        (await admin.ScalarAsync($"select count(*) from identity.administrator where email = 'x-{role}@example.com'")).ShouldBe("0");
    }

    [Fact]
    public async Task A_viewer_can_read_their_own_session_but_not_pass_the_operator_policy()
    {
        await admin.CreateAdministratorAsync("viewer-ops@example.com", "viewer", admin.TenantA);
        using var browser = admin.NewBrowser();
        await browser.SignInAsync("viewer-ops@example.com", _password);

        (await browser.GetAsync("/admin/session")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await browser.GetAsync($"{AdminFixture.TestTenantRoute}{admin.TenantA}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_operator_outside_their_grant_gets_404_and_inside_it_gets_200()
    {
        await admin.CreateAdministratorAsync("operator-grant@example.com", "operator", admin.TenantA);
        using var browser = admin.NewBrowser();
        await browser.SignInAsync("operator-grant@example.com", _password);

        var inside = await browser.GetAsync($"{AdminFixture.TestTenantRoute}{admin.TenantA}");
        var outside = await browser.GetAsync($"{AdminFixture.TestTenantRoute}{admin.TenantB}");
        var nonexistent = await browser.GetAsync($"{AdminFixture.TestTenantRoute}{Guid.CreateVersion7()}");

        inside.StatusCode.ShouldBe(HttpStatusCode.OK);
        outside.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AdminBrowser.ErrorCodeAsync(outside)).ShouldBe("not_found");
        // A foreign Tenant is indistinguishable from one that does not exist.
        nonexistent.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AdminBrowser.JsonAsync(outside)).GetProperty("detail").GetString()
            .ShouldBe((await AdminBrowser.JsonAsync(nonexistent)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task An_owner_reaches_every_tenant()
    {
        using var owner = await admin.SignedInOwnerAsync();

        (await owner.GetAsync($"{AdminFixture.TestTenantRoute}{admin.TenantA}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.GetAsync($"{AdminFixture.TestTenantRoute}{admin.TenantB}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Deactivating_an_administrator_revokes_their_open_session_at_once()
    {
        var id = await admin.CreateAdministratorAsync("soon-gone@example.com", "operator", admin.TenantA);
        using var victim = admin.NewBrowser();
        await victim.SignInAsync("soon-gone@example.com", _password);
        (await victim.GetAsync("/admin/session")).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var owner = await admin.SignedInOwnerAsync();
        (await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/administrators/{id}", new { active = false })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await victim.GetAsync("/admin/session")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var again = admin.NewBrowser();
        using var signIn = await again.SignInRawAsync("soon-gone@example.com", _password);
        signIn.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_role_change_applies_to_the_open_session_on_the_next_request()
    {
        var id = await admin.CreateAdministratorAsync("promoted@example.com", "viewer", admin.TenantA);
        using var browser = admin.NewBrowser();
        await browser.SignInAsync("promoted@example.com", _password);
        (await browser.GetAsync($"{AdminFixture.TestTenantRoute}{admin.TenantA}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var owner = await admin.SignedInOwnerAsync();
        await owner.SendJsonAsync(HttpMethod.Patch, $"/admin/administrators/{id}", new { role = "operator" });

        (await browser.GetAsync($"{AdminFixture.TestTenantRoute}{admin.TenantA}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task ShouldBeAntiforgeryProblemAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().ShouldBe("validation_failed");
        body.GetProperty("errors").TryGetProperty("X-XSRF-TOKEN", out _).ShouldBeTrue();
    }
}
