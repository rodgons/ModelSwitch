using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.Api;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.MockProvider;
using Npgsql;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Infrastructure;

/// <summary>
/// Two api hosts (A and B, as in a scaled-out deployment) on one migrated database with a bootstrapped Owner, one Tenant
/// with an API Key, and an in-process Mock Provider. Admin calls go to host A; inference can go to either. The catalog and
/// routing are empty: tests build them through the admin API.
/// </summary>
public sealed class CatalogFixture(PostgresFixture postgres, ValkeyFixture valkey) : IAsyncLifetime
{
    public const string OwnerEmail = "owner@example.com";
    public const string OwnerPassword = "the-owner-password-1";
    public const string Password = "an-ordinary-password-1";

    private static readonly string _masterKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private RunningMockProvider? _mock;
    private WebApplicationFactory<ApiHost>? _hostA;
    private WebApplicationFactory<ApiHost>? _hostB;

    public string ConnectionString { get; private set; } = null!;

    public string ApiKey { get; private set; } = null!;

    public Guid TenantId { get; private set; }

    /// <summary>The Mock Provider's OpenAI-protocol base URL (loopback, allow-listed for the hosts).</summary>
    public string OpenAiEndpoint => new Uri(_mock!.BaseAddress, "v1").ToString();

    public IServiceProvider ServicesA => _hostA!.Services;

    public IServiceProvider ServicesB => _hostB!.Services;

    public async ValueTask InitializeAsync()
    {
        _mock = await MockProviderApp.StartAsync();
        ConnectionString = await postgres.CreateMigratedDatabaseAsync();
        await using (var services = ModuleServices.ForMigrator(ConnectionString))
        {
            var outcome = await services.GetRequiredService<IOwnerBootstrapper>().BootstrapAsync(OwnerEmail, OwnerPassword, CancellationToken.None);
            outcome.Value.ShouldBe(BootstrapOutcome.Created);
            var key = await services.GetRequiredService<IIdentitySeeder>().SeedAsync("acme", "full", CancellationToken.None);
            ApiKey = key.PlaintextKey;
            TenantId = key.TenantId;
        }

        _hostA = new Factory(ConnectionString, valkey.ConnectionString);
        _hostB = new Factory(ConnectionString, valkey.ConnectionString);
        _ = _hostA.Services;
        _ = _hostB.Services;
    }

    public async ValueTask DisposeAsync()
    {
        if (_hostA is not null)
        {
            await _hostA.DisposeAsync();
        }

        if (_hostB is not null)
        {
            await _hostB.DisposeAsync();
        }

        if (_mock is not null)
        {
            await _mock.DisposeAsync();
        }
    }

    public Task<AdminBrowser> OwnerAsync() => SignInAsync(OwnerEmail, OwnerPassword);

    /// <summary>Creates an Administrator as the Owner and returns a browser signed in as it.</summary>
    public async Task<AdminBrowser> AdministratorAsync(string role)
    {
        var email = $"{role}-{Guid.CreateVersion7():N}@example.com";
        using (var owner = await OwnerAsync())
        {
            var response = await owner.SendJsonAsync(HttpMethod.Post, "/admin/administrators", new { email, password = Password, role });
            response.EnsureSuccessStatusCode();
        }

        return await SignInAsync(email, Password);
    }

    /// <summary>An inference client on host B (the host that did not take the admin write).</summary>
    public HttpClient InferenceClientB()
    {
        var client = _hostB!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        return client;
    }

    public async Task<string> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private async Task<AdminBrowser> SignInAsync(string email, string password)
    {
        var browser = new AdminBrowser(_hostA!.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false,
        }));
        await browser.SignInAsync(email, password);
        return browser;
    }

    private sealed class Factory(string connectionString, string valkeyConnectionString) : WebApplicationFactory<ApiHost>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString,
                ["Security:ApiKeyPepper"] = ModuleServices.Pepper,
                ["Security:MasterKey"] = _masterKey,
                // Only this exact host skips the SSRF checks, so 127.0.0.2 and localhost still prove the policy applies.
                ["Outbound:AllowedPrivateHosts"] = "127.0.0.1",
                ["ConnectionStrings:Valkey"] = valkeyConnectionString,
                ["Limits:ValkeyTimeoutMs"] = "1000",
            }));
    }
}
