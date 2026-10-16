using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ModelSwitch.FailureInjectionTests.Support;

/// <summary>
/// A stand-in OTLP/HTTP telemetry backend on loopback for the degradation scenarios. It counts the exports it receives per signal,
/// can be stopped mid-load like a killed container, or can accept connections and never answer, which is the harsher failure for an
/// exporter (it waits for its timeout instead of failing fast).
/// </summary>
public sealed class FakeOtlpBackend : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, int> _exports = new(StringComparer.Ordinal);

    private FakeOtlpBackend(WebApplication app, bool hang)
    {
        _app = app;
        _app.MapPost("/v1/{signal}", async (string signal, HttpContext http) =>
        {
            _exports.AddOrUpdate(signal, 1, (_, count) => count + 1);
            if (hang)
            {
                await Task.Delay(Timeout.Infinite, http.RequestAborted);
            }

            http.Response.ContentType = "application/x-protobuf";
            return Results.Empty;
        });
    }

    public Uri Endpoint { get; private set; } = null!;

    /// <summary>Exports received for <c>traces</c>, <c>metrics</c> or <c>logs</c>.</summary>
    public int Exports(string signal) => _exports.TryGetValue(signal, out var count) ? count : 0;

    public static async Task<FakeOtlpBackend> StartAsync(bool hang = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var backend = new FakeOtlpBackend(builder.Build(), hang);
        await backend._app.StartAsync();
        var address = backend._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        backend.Endpoint = new Uri(address);
        return backend;
    }

    /// <summary>Stops listening, as when the backend's container is killed: further exports get connection refused.</summary>
    public Task StopAsync() => _app.StopAsync();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
