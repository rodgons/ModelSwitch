using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace ModelSwitch.MockProvider;

/// <summary>
/// The Mock Provider (docs/provider-adapters.md section 5): a scriptable fake that speaks both the OpenAI-compatible and the Ollama
/// protocol. Tests host it in-process with <see cref="StartAsync"/>; the executable runs it standalone.
/// </summary>
public static class MockProviderApp
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ScenarioStore>();
        configure?.Invoke(builder);

        var app = builder.Build();

        app.MapGet("/healthz", () => Results.Text("ok"));

        // OpenAI-compatible protocol
        app.MapPost("/v1/chat/completions", OpenAiProtocol.ChatAsync);
        app.MapGet("/v1/models", OpenAiProtocol.Models);

        // Ollama protocol
        app.MapPost("/api/chat", OllamaProtocol.ChatAsync);
        app.MapGet("/api/tags", OllamaProtocol.Tags);
        app.MapGet("/api/version", OllamaProtocol.Version);

        // Control endpoint for full-stack tests, where the gateway never forwards X-Mock-* headers.
        app.MapPost("/__scenarios", (JsonObject body, ScenarioStore store) =>
        {
            var model = body["model"]?.GetValue<string>();
            var fault = body["fault"]?.GetValue<string>();
            string? error = null;
            if (string.IsNullOrWhiteSpace(model))
            {
                error = "The model is required.";
            }
            else
            {
                _ = FaultSpec.TryParse(fault, out _, out error);
            }

            if (error is not null)
            {
                return Results.BadRequest(new JsonObject { ["error"] = error });
            }

            store.SetFault(model!, fault!);
            return Results.NoContent();
        });
        app.MapGet("/__stats", (ScenarioStore store) => Results.Json(new JsonObject
        {
            ["started"] = JsonSerializer.SerializeToNode(store.Started()),
            ["aborted"] = JsonSerializer.SerializeToNode(store.Aborted()),
        }));
        app.MapDelete("/__scenarios", (ScenarioStore store) =>
        {
            store.Clear();
            return Results.NoContent();
        });

        return app;
    }

    /// <summary>Starts the Mock Provider on a free loopback port and returns a handle that stops it when disposed.</summary>
    public static async Task<RunningMockProvider> StartAsync(CancellationToken ct = default)
    {
        var app = Build([], builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
        });
        await app.StartAsync(ct);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new RunningMockProvider(app, new Uri(address));
    }
}

/// <summary>An in-process Mock Provider listening on <see cref="BaseAddress"/>.</summary>
public sealed class RunningMockProvider(WebApplication app, Uri baseAddress) : IAsyncDisposable
{
    public Uri BaseAddress { get; } = baseAddress;

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
