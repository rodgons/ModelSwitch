using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.Api;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.MockProvider;
using Npgsql;

namespace ModelSwitch.IntegrationTests.Infrastructure;

/// <summary>
/// The real api host (<see cref="WebApplicationFactory{TEntryPoint}"/>) on a migrated database cloned from the template,
/// with one Tenant, two API Keys and two Providers (OpenAI-compatible and Ollama) that point at an in-process Mock Provider.
/// Static aliases: <c>balanced</c> (OpenAI protocol) and <c>local</c> (Ollama protocol). Use it as a class fixture.
/// </summary>
public sealed class GatewayFixture(PostgresFixture postgres) : IAsyncLifetime
{
    public const string ChatAlias = "balanced";
    public const string OllamaAlias = "local";

    /// <summary><c>mock-echo</c> first, then <c>mock-fallback</c>, both on the OpenAI-protocol Mock Provider.</summary>
    public const string ResilientAlias = "resilient";

    public const string PrimaryModel = "mock-echo";
    public const string FallbackModel = "mock-fallback";

    private RunningMockProvider? _mock;
    private Factory? _factory;

    /// <summary>Authenticated with a key that has every Scope.</summary>
    public HttpClient Client { get; private set; } = null!;

    /// <summary>No Authorization header.</summary>
    public HttpClient Anonymous { get; private set; } = null!;

    /// <summary>Authenticated with a key that only has <c>models:read</c>.</summary>
    public HttpClient ModelsOnly { get; private set; } = null!;

    public string ConnectionString { get; private set; } = null!;

    public Uri MockAddress => _mock!.BaseAddress;

    public IServiceProvider Services => _factory!.Services;

    public async ValueTask InitializeAsync()
    {
        _mock = await MockProviderApp.StartAsync();
        ConnectionString = await postgres.CreateMigratedDatabaseAsync();

        SeededApiKey full;
        SeededApiKey modelsOnly;
        await using (var services = ModuleServices.ForMigrator(ConnectionString))
        {
            var seeder = services.GetRequiredService<IIdentitySeeder>();
            full = await seeder.SeedAsync("acme", "full", CancellationToken.None);
            modelsOnly = await seeder.SeedAsync("acme", "models-only", CancellationToken.None);
        }

        await ExecuteAsync("update identity.api_key set scopes = array['models:read'] where name = 'models-only'");
        await InsertProviderAsync("mock-openai", "openai_compatible", new Uri(_mock.BaseAddress, "v1").ToString(), PrimaryModel, FallbackModel);
        await InsertProviderAsync("mock-ollama", "ollama", _mock.BaseAddress.ToString(), PrimaryModel);

        _factory = new Factory(ConnectionString);
        Client = Authenticated(full.PlaintextKey);
        ModelsOnly = Authenticated(modelsOnly.PlaintextKey);
        Anonymous = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        ModelsOnly?.Dispose();
        Anonymous?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_mock is not null)
        {
            await _mock.DisposeAsync();
        }
    }

    /// <summary>A client for the same host, authenticated with <paramref name="plaintextKey"/>.</summary>
    public HttpClient Authenticated(string plaintextKey)
    {
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plaintextKey);
        return client;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Scripts a fault on the Mock Provider for one of its model ids (e.g. <c>status=503;times=2</c>); null clears all faults.</summary>
    public async Task SetFaultAsync(string? model, string? fault)
    {
        using var http = new HttpClient { BaseAddress = _mock!.BaseAddress };
        var response = model is null || fault is null
            ? await http.DeleteAsync(new Uri("/__scenarios", UriKind.Relative))
            : await http.PostAsJsonAsync(new Uri("/__scenarios", UriKind.Relative), new { model, fault });
        response.EnsureSuccessStatusCode();
    }

    private async Task InsertProviderAsync(string name, string adapterType, string endpoint, params string[] models)
    {
        var providerId = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            insert into registry.provider (id, name, adapter_type, endpoint, request_timeout_ms, enabled, created_at, updated_at)
            values (@id, @name, @type, @endpoint, 10000, true, now(), now());
            """,
            connection);
        command.Parameters.AddWithValue("id", providerId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("type", adapterType);
        command.Parameters.AddWithValue("endpoint", endpoint);
        await command.ExecuteNonQueryAsync();

        foreach (var model in models)
        {
            await using var insertModel = new NpgsqlCommand(
                """
                insert into registry.provider_model (id, provider_id, provider_model_id, display_name, capabilities, enabled, created_at, updated_at)
                values (@modelId, @id, @model, @model, '{}'::jsonb, true, now(), now());
                """,
                connection);
            insertModel.Parameters.AddWithValue("id", providerId);
            insertModel.Parameters.AddWithValue("modelId", Guid.CreateVersion7());
            insertModel.Parameters.AddWithValue("model", model);
            await insertModel.ExecuteNonQueryAsync();
        }
    }

    private sealed class Factory(string connectionString) : WebApplicationFactory<ApiHost>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString,
                ["Security:ApiKeyPepper"] = ModuleServices.Pepper,
                // The Mock Provider listens on loopback, which the outbound policy refuses unless allow-listed.
                ["Outbound:AllowedPrivateHosts"] = "127.0.0.1",
                [$"Inference:StaticAliases:{ChatAlias}"] = "mock-openai/mock-echo",
                [$"Inference:StaticAliases:{OllamaAlias}"] = "mock-ollama/mock-echo",
                [$"Inference:StaticAliases:{ResilientAlias}"] = $"mock-openai/{PrimaryModel},mock-openai/{FallbackModel}",
            }));
        }
    }
}
