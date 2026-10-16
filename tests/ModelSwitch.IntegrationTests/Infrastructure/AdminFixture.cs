using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.Api;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel.Http;
using Npgsql;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Infrastructure;

/// <summary>
/// The real api host on a migrated database with a bootstrapped Owner, two Tenants, and a test-only tenant-scoped route
/// (<c>GET /admin/_test/tenants/{tenantId}</c>, Operator policy + Tenant Grant check) standing in for the tenant endpoints of
/// later phases. Browser-like clients keep cookies and talk to an <c>https</c> base address so <c>Secure</c> cookies are sent.
/// </summary>
public sealed class AdminFixture(PostgresFixture postgres) : IAsyncLifetime
{
    public const string OwnerEmail = "owner@example.com";
    public const string OwnerPassword = "the-owner-password-1";
    public const string SourceAddress = "203.0.113.9";
    public const string TestTenantRoute = "/admin/_test/tenants/";

    private Factory? _factory;

    public string ConnectionString { get; private set; } = null!;

    public Guid TenantA { get; private set; }

    public Guid TenantB { get; private set; }

    public IServiceProvider Services => _factory!.Services;

    public async ValueTask InitializeAsync()
    {
        ConnectionString = await postgres.CreateMigratedDatabaseAsync();
        TenantA = await InsertTenantAsync("tenant-a");
        TenantB = await InsertTenantAsync("tenant-b");

        await using (var services = ModuleServices.ForMigrator(ConnectionString))
        {
            var outcome = await services.GetRequiredService<IOwnerBootstrapper>().BootstrapAsync(OwnerEmail, OwnerPassword, CancellationToken.None);
            outcome.Value.ShouldBe(BootstrapOutcome.Created);
        }

        _factory = new Factory(ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    /// <summary>A fresh browser: its own cookie jar, no redirects followed.</summary>
    public AdminBrowser NewBrowser() => new(_factory!.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = true,
        AllowAutoRedirect = false,
    }));

    /// <summary>Signs in as the bootstrapped Owner.</summary>
    public async Task<AdminBrowser> SignedInOwnerAsync()
    {
        var browser = NewBrowser();
        await browser.SignInAsync(OwnerEmail, OwnerPassword);
        return browser;
    }

    /// <summary>Creates an Administrator through the API as the Owner and returns its id.</summary>
    public async Task<Guid> CreateAdministratorAsync(string email, string role, params Guid[] tenantIds)
    {
        using var owner = await SignedInOwnerAsync();
        var response = await owner.SendJsonAsync(
            HttpMethod.Post,
            "/admin/administrators",
            new { email, password = "an-ordinary-password-1", role, tenantIds });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Creates an Administrator of <paramref name="role"/> with a grant to <paramref name="tenantIds"/> and signs it in.</summary>
    public async Task<AdminBrowser> SignedInAsync(string email, string role, params Guid[] tenantIds)
        => await SignedInAsync(email, role, allTenants: false, tenantIds);

    /// <summary>Like the overload above, with an all-Tenants grant when <paramref name="allTenants"/> is true.</summary>
    public async Task<AdminBrowser> SignedInAsync(string email, string role, bool allTenants, params Guid[] tenantIds)
    {
        using (var owner = await SignedInOwnerAsync())
        {
            var created = await owner.SendJsonAsync(HttpMethod.Post, "/admin/administrators", new { email, password = "an-ordinary-password-1", role, allTenants, tenantIds });
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var browser = NewBrowser();
        await browser.SignInAsync(email, "an-ordinary-password-1");
        return browser;
    }

    /// <summary>
    /// Another api host on the same database, as a second instance would be. <paramref name="configure"/> may replace services
    /// (a <see cref="TimeProvider"/>, a decorated contract).
    /// </summary>
    public WebApplicationFactory<ApiHost> StartHost(Action<IServiceCollection>? configure = null) => new Factory(ConnectionString, configure);

    /// <summary>A browser-like client of <paramref name="host"/>, with its own cookie jar.</summary>
    public static AdminBrowser NewBrowser(WebApplicationFactory<ApiHost> host) => new(host.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = true,
        AllowAutoRedirect = false,
    }));

    /// <summary>A client for the inference API carrying <paramref name="apiKey"/> as its bearer token (none when null).</summary>
    public HttpClient NewApiClient(string? apiKey)
    {
        var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }

        return client;
    }

    public async Task<string> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private async Task<Guid> InsertTenantAsync(string name)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("insert into identity.tenant (id, name, status, created_at, updated_at) values (@id, @name, 'active', now(), now())", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private sealed class Factory(string connectionString, Action<IServiceCollection>? configure = null) : WebApplicationFactory<ApiHost>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString,
                ["Security:ApiKeyPepper"] = ModuleServices.Pepper,
                // The admin tests never reach the limits step; the setting only has to be present.
                ["ConnectionStrings:Valkey"] = "localhost:1",
            }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter, TestRoutes>();
                configure?.Invoke(services);
            });
        }
    }

    private sealed class TestRoutes : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // TestServer has no peer address; give every request one so the audit record's source_ip (inet) is exercised.
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(SourceAddress);
                return nextMiddleware(context);
            });
            next(app);
            app.UseEndpoints(endpoints => endpoints
                .MapGet(
                    TestTenantRoute + "{tenantId:guid}",
                    (HttpContext http, Guid tenantId, ITenantGrantChecker grants) =>
                    {
                        var access = grants.CheckAccess(tenantId);
                        return access.IsFailure ? ProblemResults.From(http, access.Error) : Results.Ok(new { tenantId });
                    })
                .RequireAuthorization(AdminPolicies.Operator));
        };
    }
}

/// <summary>A cookie-keeping client that remembers the antiforgery token, like the dashboard does.</summary>
public sealed class AdminBrowser(HttpClient client) : IDisposable
{
    private string? _token;

    public HttpClient Client { get; } = client;

    public string? AntiforgeryToken => _token;

    /// <summary>The session cookie as a <c>Cookie</c> header value, captured when signing in (the handler's jar is not reachable).</summary>
    public string? SessionCookieHeader { get; private set; }

    public void Dispose() => Client.Dispose();

    public async Task<string> FetchAntiforgeryTokenAsync()
    {
        var body = await Client.GetFromJsonAsync<JsonElement>("/admin/antiforgery-token");
        _token = body.GetProperty("token").GetString()!;
        return _token;
    }

    /// <summary>The documented flow: get a token, POST the credentials, keep the token that comes back for the new session.</summary>
    public async Task<HttpResponseMessage> SignInRawAsync(string? email, string? password)
    {
        await FetchAntiforgeryTokenAsync();
        var response = await SendJsonAsync(HttpMethod.Post, "/admin/session", new { email, password });
        await response.Content.LoadIntoBufferAsync();
        if (response.IsSuccessStatusCode)
        {
            SessionCookieHeader = response.Headers.GetValues("Set-Cookie")
                .Single(c => c.StartsWith("__Host-ms_session=", StringComparison.Ordinal))
                .Split(';')[0];
            _token = (await JsonAsync(response)).GetProperty("antiforgeryToken").GetString();
        }

        return response;
    }

    public async Task SignInAsync(string email, string password)
    {
        using var response = await SignInRawAsync(email, password);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    public Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, object? body = null, bool withToken = true)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = body as HttpContent ?? JsonContent.Create(body);
        }

        if (withToken && _token is not null)
        {
            request.Headers.Add("X-XSRF-TOKEN", _token);
        }

        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => Client.GetAsync(path);

    // Through a string so a response can be read more than once (the content stream is single-use).
    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

    public static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
        => (await JsonAsync(response)).GetProperty("errorCode").GetString() ?? string.Empty;
}
