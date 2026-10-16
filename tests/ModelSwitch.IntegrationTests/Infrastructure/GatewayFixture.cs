using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.Api;
using ModelSwitch.Configuration.Contracts;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.MockProvider;
using ModelSwitch.SharedKernel.Configuration;
using Npgsql;

namespace ModelSwitch.IntegrationTests.Infrastructure;

/// <summary>
/// The real api host (<see cref="WebApplicationFactory{TEntryPoint}"/>) on a migrated database cloned from the template,
/// with one Tenant, two API Keys and two Providers (OpenAI-compatible and Ollama) that point at an in-process Mock Provider.
/// Routing is real: each alias is a <c>routing_alias</c> row over a static Routing Policy, and the models are priced and declare
/// their Capabilities, so they are eligible. <c>balanced</c> (OpenAI protocol), <c>local</c> (Ollama protocol) and
/// <c>resilient</c> (OpenAI protocol with a Fallback). Use it as a class fixture.
/// </summary>
public sealed class GatewayFixture(PostgresFixture postgres, ValkeyFixture valkey) : IAsyncLifetime
{
    public const string ChatAlias = "balanced";
    public const string OllamaAlias = "local";

    /// <summary><c>mock-echo</c> first, then <c>mock-fallback</c>, both on the OpenAI-protocol Mock Provider.</summary>
    public const string ResilientAlias = "resilient";

    public const string PrimaryModel = "mock-echo";
    public const string FallbackModel = "mock-fallback";

    private RunningMockProvider? _mock;
    private WebApplicationFactory<ApiHost>? _factory;

    /// <summary>Authenticated with a key that has every Scope.</summary>
    public HttpClient Client { get; private set; } = null!;

    /// <summary>No Authorization header.</summary>
    public HttpClient Anonymous { get; private set; } = null!;

    /// <summary>Authenticated with a key that only has <c>models:read</c>.</summary>
    public HttpClient ModelsOnly { get; private set; } = null!;

    public string ConnectionString { get; private set; } = null!;

    /// <summary>The Valkey the hosts use. The shared container unless a test points it elsewhere before <see cref="InitializeAsync"/>.</summary>
    public string? ValkeyConnectionString { get; set; }

    /// <summary>Extra deployment settings (for example <c>Limits:FailureMode</c>) for every host, set before <see cref="InitializeAsync"/>.</summary>
    public Dictionary<string, string?> Settings { get; } = [];

    /// <summary>The seeded Tenant (<c>acme</c>) that both API Keys belong to.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>The seeded full-scope API Key, for clients of additional hosts.</summary>
    public string FullKey { get; private set; } = null!;

    public Uri MockAddress => _mock!.BaseAddress;

    /// <summary>The Mock Provider's counters (<c>GET /__stats</c>): requests started and aborted by the client, per model id.</summary>
    public async Task<(IReadOnlyDictionary<string, int> Started, IReadOnlyDictionary<string, int> Aborted)> MockStatsAsync()
    {
        using var http = new HttpClient { BaseAddress = _mock!.BaseAddress };
        var stats = await http.GetFromJsonAsync<System.Text.Json.JsonElement>(new Uri("/__stats", UriKind.Relative));
        static Dictionary<string, int> Read(System.Text.Json.JsonElement e) => e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        return (Read(stats.GetProperty("started")), Read(stats.GetProperty("aborted")));
    }

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
        var openAiModels = await InsertProviderAsync("mock-openai", "openai_compatible", new Uri(_mock.BaseAddress, "v1").ToString(), PrimaryModel, FallbackModel);
        var ollamaModels = await InsertProviderAsync("mock-ollama", "ollama", _mock.BaseAddress.ToString(), PrimaryModel);
        await WriteConfigurationAsync(
            PolicyAndAliasSql(ChatAlias, [("mock-openai", PrimaryModel, openAiModels[PrimaryModel])])
            + PolicyAndAliasSql(OllamaAlias, [("mock-ollama", PrimaryModel, ollamaModels[PrimaryModel])])
            + PolicyAndAliasSql(ResilientAlias, [("mock-openai", PrimaryModel, openAiModels[PrimaryModel]), ("mock-openai", FallbackModel, openAiModels[FallbackModel])]));

        TenantId = Guid.Parse(await ScalarAsync("select id from identity.tenant where name = 'acme'"));
        FullKey = full.PlaintextKey;
        _factory = StartHost();
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

    /// <summary>Another api host on the same database and Valkey, as a second instance of a scaled-out deployment. The caller disposes it.</summary>
    public WebApplicationFactory<ApiHost> StartHost() => new Factory(ConnectionString, ValkeyConnectionString ?? valkey.ConnectionString, Settings);

    /// <summary>A client for <paramref name="host"/>, authenticated with the seeded full-scope API Key.</summary>
    public HttpClient ClientFor(WebApplicationFactory<ApiHost> host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FullKey);
        return client;
    }

    /// <summary>
    /// A new Tenant with a full-scope API Key and the given limits (null: unlimited), live in every running host. Each test uses its
    /// own Tenant, so the Valkey counters of one test never reach another.
    /// </summary>
    public async Task<(Guid TenantId, string Key)> SeedTenantAsync(string name, int? requestsPerMinute = null, int? maxConcurrent = null)
    {
        SeededApiKey seeded;
        await using (var services = ModuleServices.ForMigrator(ConnectionString))
        {
            seeded = await services.GetRequiredService<IIdentitySeeder>().SeedAsync(name, "key", CancellationToken.None);
        }

        await SetLimitsAsync(seeded.TenantId, requestsPerMinute, maxConcurrent);
        return (seeded.TenantId, seeded.PlaintextKey);
    }

    /// <summary>Replaces the Tenant's limits row, as the limits admin endpoint will, and waits for the hosts' snapshots.</summary>
    public Task SetLimitsAsync(Guid tenantId, int? requestsPerMinute, int? maxConcurrent)
    {
        static string Sql(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";
        return WriteConfigurationAsync(
            $"""
            delete from identity.tenant_limits where tenant_id = '{tenantId}';
            insert into identity.tenant_limits (tenant_id, requests_per_minute, max_concurrent, updated_at)
            values ('{tenantId}', {Sql(requestsPerMinute)}, {Sql(maxConcurrent)}, now());
            """);
    }

    public async Task<string> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
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

    /// <summary>
    /// Applies <paramref name="sql"/> and bumps <c>config.config_version</c> in one transaction, as an admin write does. When the
    /// host is already running, waits until its snapshot has caught up.
    /// </summary>
    public async Task WriteConfigurationAsync(string sql)
    {
        long version;
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                await command.ExecuteNonQueryAsync();
            }

            version = await ConfigVersion.BumpAsync(connection, transaction, CancellationToken.None);
            await transaction.CommitAsync();
        }

        if (_factory is not null)
        {
            var snapshots = Services.GetRequiredService<IConfigurationSnapshotProvider>();
            var deadline = Environment.TickCount64 + 5_000;
            while (snapshots.Current.Version < version && Environment.TickCount64 < deadline)
            {
                await Task.Delay(10);
            }
        }
    }

    /// <summary>SQL for a static Routing Policy over the given <c>(provider, model, id)</c> Candidates and an alias that points to it.</summary>
    public static string PolicyAndAliasSql(string alias, (string Provider, string Model, Guid Id)[] candidates, string execution = "", string rootExtra = "")
    {
        var policyId = Guid.CreateVersion7();
        var names = string.Join(',', candidates.Select(c => $"\"{c.Provider}/{c.Model}\""));
        var resolved = string.Join(',', candidates.Select(c => $"\"{c.Provider}/{c.Model}\":\"{c.Id}\""));
        var executionJson = execution.Length == 0 ? string.Empty : $",\"execution\":{execution}";
        return $$"""
            insert into routing.routing_policy values ('{{policyId}}', '{{alias}}-policy', 1, now());
            insert into routing.routing_policy_version (policy_id, version, definition, resolved_candidates, created_at)
              values ('{{policyId}}', 1, '{"strategy":"static","candidates":[{{names}}]{{rootExtra}}{{executionJson}}}', '{{{resolved}}}', now());
            insert into routing.model_alias (alias, policy_id, description, updated_at) values ('{{alias}}', '{{policyId}}', null, now());

            """;
    }

    private async Task<Dictionary<string, Guid>> InsertProviderAsync(string name, string adapterType, string endpoint, params string[] models)
    {
        var modelIds = new Dictionary<string, Guid>();
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
            var modelId = Guid.CreateVersion7();
            modelIds[model] = modelId;
            await using var insertModel = new NpgsqlCommand(
                """
                insert into registry.provider_model (id, provider_id, provider_model_id, display_name, capabilities, enabled, created_at, updated_at)
                values (@modelId, @id, @model, @model, @capabilities::jsonb, true, now(), now());
                insert into registry.model_price values (gen_random_uuid(), @modelId, 1, null, 2, 'USD', '2026-01-01', now(), null);
                """,
                connection);
            insertModel.Parameters.AddWithValue("id", providerId);
            insertModel.Parameters.AddWithValue("modelId", modelId);
            insertModel.Parameters.AddWithValue("model", model);
            insertModel.Parameters.AddWithValue("capabilities", _capabilities);
            await insertModel.ExecuteNonQueryAsync();
        }

        return modelIds;
    }

    private const string _capabilities =
        """{"streaming":true,"jsonObject":true,"jsonSchema":true,"contextWindow":128000,"maxOutputTokens":16384,"tokenizer":"o200k_base","parameters":["temperature","top_p","seed","stop","presence_penalty","frequency_penalty"]}""";

    private sealed class Factory(string connectionString, string valkeyConnectionString, IReadOnlyDictionary<string, string?> settings) : WebApplicationFactory<ApiHost>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString,
                ["Security:ApiKeyPepper"] = ModuleServices.Pepper,
                // The Mock Provider listens on loopback, which the outbound policy refuses unless allow-listed.
                ["Outbound:AllowedPrivateHosts"] = "127.0.0.1",
                // One second instead of 15 so the keep-alive test does not take a quarter of a minute.
                ["Streaming:KeepAliveSeconds"] = "1",
                ["ConnectionStrings:Valkey"] = valkeyConnectionString,
                // A cold first call (connect, script load) in CI can exceed the production 50 ms budget.
                ["Limits:ValkeyTimeoutMs"] = "1000",
            }.Concat(settings)));
        }
    }
}
