using System.Text.Json.Nodes;
using ModelSwitch.SharedKernel.Http;

namespace ModelSwitch.Api.Endpoints;

/// <summary>
/// <c>GET /health</c> (liveness) and <c>GET /ready</c> (docs/api.md section 4). Both are unauthenticated and their bodies
/// never contain secrets. Readiness is every applicable <see cref="IReadinessCheck"/> (Valkey only when
/// the limits fail closed); Provider status is deliberately not part of readiness.
/// </summary>
internal static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Json(new JsonObject { ["status"] = "ok" }));
        app.MapGet("/ready", ReadyAsync);
        return app;
    }

    private static async Task<IResult> ReadyAsync(IEnumerable<IReadinessCheck> checks, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(checks.Where(check => check.Applies).Select(async check => (check.Name, Ok: await check.CheckAsync(cancellationToken))));
        var ready = results.All(r => r.Ok);
        var body = new JsonObject
        {
            ["status"] = ready ? "ready" : "not_ready",
            ["checks"] = new JsonObject(results.Select(r => KeyValuePair.Create(r.Name, (JsonNode?)(r.Ok ? "ok" : "unavailable")))),
        };

        return Results.Json(body, statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }
}
