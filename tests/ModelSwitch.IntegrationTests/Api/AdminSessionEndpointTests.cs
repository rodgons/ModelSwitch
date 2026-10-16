using System.Net;
using ModelSwitch.IntegrationTests.Infrastructure;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>Sign-in, sign-out, the current Administrator and the antiforgery token (api.md section 3, security.md section 3).</summary>
public sealed class AdminSessionEndpointTests(AdminFixture admin) : IClassFixture<AdminFixture>
{
    [Fact]
    [HappyPath("GET /admin/antiforgery-token")]
    public async Task The_antiforgery_token_is_available_before_signing_in_and_sets_a_host_prefixed_cookie()
    {
        using var browser = admin.NewBrowser();

        var response = await browser.GetAsync("/admin/antiforgery-token");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        (await AdminBrowser.JsonAsync(response)).GetProperty("token").GetString().ShouldNotBeNullOrWhiteSpace();
        var cookie = SetCookie(response, "__Host-ms_xsrf");
        cookie.ShouldContain("secure", Case.Insensitive);
        cookie.ShouldContain("samesite=strict", Case.Insensitive);
    }

    [Fact]
    [HappyPath("POST /admin/session")]
    public async Task Signing_in_returns_the_administrator_and_a_token_and_sets_an_http_only_strict_host_cookie()
    {
        using var browser = admin.NewBrowser();

        using var response = await browser.SignInRawAsync("  Owner@Example.com ", AdminFixture.OwnerPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var body = await AdminBrowser.JsonAsync(response);
        body.GetProperty("administrator").GetProperty("email").GetString().ShouldBe(AdminFixture.OwnerEmail);
        body.GetProperty("administrator").GetProperty("role").GetString().ShouldBe("owner");
        body.GetProperty("administrator").GetProperty("allTenants").GetBoolean().ShouldBeTrue();
        body.GetProperty("antiforgeryToken").GetString().ShouldNotBeNullOrWhiteSpace();

        var cookie = SetCookie(response, "__Host-ms_session");
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("secure", Case.Insensitive);
        cookie.ShouldContain("samesite=strict", Case.Insensitive);
        cookie.ShouldContain("path=/", Case.Insensitive);
        cookie.ShouldNotContain("domain=", Case.Insensitive);
        cookie.ShouldNotContain(AdminFixture.OwnerEmail); // an opaque key, not the ticket
    }

    [Fact]
    [HappyPath("GET /admin/session")]
    public async Task The_current_administrator_comes_with_a_token_that_works_for_unsafe_requests()
    {
        using var browser = await admin.SignedInOwnerAsync();

        var response = await browser.GetAsync("/admin/session");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await AdminBrowser.JsonAsync(response);
        body.GetProperty("administrator").GetProperty("email").GetString().ShouldBe(AdminFixture.OwnerEmail);
        var token = body.GetProperty("antiforgeryToken").GetString()!;

        using var signOut = new HttpRequestMessage(HttpMethod.Delete, "/admin/session");
        signOut.Headers.Add("X-XSRF-TOKEN", token);
        (await browser.Client.SendAsync(signOut, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    [HappyPath("DELETE /admin/session")]
    public async Task Signing_out_revokes_the_session_so_the_old_cookie_no_longer_works()
    {
        using var browser = await admin.SignedInOwnerAsync();
        var cookie = browser.SessionCookieHeader ?? throw new InvalidOperationException("No session cookie.");

        var signOut = await browser.SendJsonAsync(HttpMethod.Delete, "/admin/session");

        signOut.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await browser.GetAsync("/admin/session")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The stolen-cookie replay: a new browser presenting the old cookie value is refused, server side.
        using var replay = admin.NewBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/session");
        request.Headers.Add("Cookie", cookie);
        var replayed = await replay.Client.SendAsync(request, TestContext.Current.CancellationToken);
        replayed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Without_a_session_the_answer_is_a_401_problem_not_a_redirect()
    {
        using var browser = admin.NewBrowser();

        var response = await browser.GetAsync("/admin/session");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Location.ShouldBeNull();
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await AdminBrowser.ErrorCodeAsync(response)).ShouldBe("unauthenticated");
        response.Headers.Contains("X-Request-Id").ShouldBeTrue();
    }

    [Fact]
    public async Task An_api_key_is_not_an_administrator_session()
    {
        using var browser = admin.NewBrowser();
        browser.Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "msk_abcdefgh_" + new string('A', 43));

        (await browser.GetAsync("/admin/session")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Missing_credentials_are_a_400_validation_problem_with_an_errors_extension()
    {
        using var browser = admin.NewBrowser();

        using var response = await browser.SignInRawAsync(email: null, password: "");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await AdminBrowser.JsonAsync(response);
        body.GetProperty("errorCode").GetString().ShouldBe("validation_failed");
        body.GetProperty("errors").TryGetProperty("email", out _).ShouldBeTrue();
        body.GetProperty("errors").TryGetProperty("password", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_email_are_the_same_401()
    {
        using var browser = admin.NewBrowser();

        using var wrong = await browser.SignInRawAsync(AdminFixture.OwnerEmail, "definitely-not-it-1");
        using var unknown = await browser.SignInRawAsync("nobody@example.com", "definitely-not-it-1");

        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AdminBrowser.JsonAsync(wrong)).GetProperty("detail").GetString()
            .ShouldBe((await AdminBrowser.JsonAsync(unknown)).GetProperty("detail").GetString());
        foreach (var response in new[] { wrong, unknown })
        {
            response.Headers.TryGetValues("Set-Cookie", out var cookies);
            (cookies ?? []).ShouldNotContain(c => c.StartsWith("__Host-ms_session", StringComparison.Ordinal));
        }

        (await browser.GetAsync("/admin/session")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_so_even_the_right_one_is_refused()
    {
        var email = "lockme@example.com";
        await admin.CreateAdministratorAsync(email, "viewer");
        using var browser = admin.NewBrowser();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await browser.SignInRawAsync(email, "wrong-password-1234");
            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var correct = await browser.SignInRawAsync(email, "an-ordinary-password-1");
        correct.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await admin.ScalarAsync($"select locked_until is not null from identity.administrator where email = '{email}'")).ShouldBe("True");
    }

    [Fact]
    public async Task Sign_in_is_audited_with_request_id_and_source_and_never_the_password()
    {
        using var browser = admin.NewBrowser();
        using var response = await browser.SignInRawAsync(AdminFixture.OwnerEmail, AdminFixture.OwnerPassword);
        var requestId = response.Headers.GetValues("X-Request-Id").Single();

        (await admin.ScalarAsync($"select count(*) from identity.audit_record where action = 'administrator.signed_in' and request_id = '{requestId}' and administrator_id is not null")).ShouldBe("1");
        (await admin.ScalarAsync("select count(*) from identity.audit_record where details::text ilike '%" + AdminFixture.OwnerPassword + "%'")).ShouldBe("0");
    }

    [Fact]
    public async Task A_session_cookie_cannot_be_forged_from_a_valid_looking_value()
    {
        using var browser = admin.NewBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/session");
        request.Headers.Add("Cookie", "__Host-ms_session=CfDJ8" + new string('A', 120));

        (await browser.Client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static string SetCookie(HttpResponseMessage response, string name)
        => response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(name + "=", StringComparison.Ordinal));
}
