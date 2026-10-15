using System.Text.Json;
using System.Text.Json.Nodes;

namespace ModelSwitch.MockProvider;

/// <summary>The Ollama wire protocol: <c>/api/chat</c> (JSON or NDJSON), <c>/api/tags</c> and <c>/api/version</c>.</summary>
internal sealed class OllamaProtocol(TimeProvider time) : IMockProtocol
{
    public static async Task<IResult> ChatAsync(HttpContext http, ScenarioStore store, TimeProvider time)
    {
        JsonNode? body;
        try
        {
            body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new JsonObject { ["error"] = "The request body is not valid JSON." });
        }

        if (body is not JsonObject request)
        {
            return Results.BadRequest(new JsonObject { ["error"] = "The request body must be a JSON object." });
        }

        var messages = (request["messages"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(m => new MockMessage(m["role"]?.GetValue<string>() ?? "user", m["content"]?.GetValue<string>() ?? string.Empty))
            .ToList();
        var stream = request["stream"]?.GetValue<bool>() ?? true;
        var maxTokens = request["options"]?["num_predict"]?.GetValue<int>();

        if (!PlanResolver.TryResolve(http, store, request["model"]?.GetValue<string>(), messages, maxTokens, stream, true, out var plan, out var error))
        {
            return Results.BadRequest(new JsonObject { ["error"] = error });
        }

        await MockPipeline.RunAsync(http, plan!, new OllamaProtocol(time), time);
        return Results.Empty;
    }

    public static IResult Tags(TimeProvider time)
    {
        var modified = time.GetUtcNow().ToString("O");
        var models = new JsonArray(OpenAiProtocol.ModelIds.Select(id => (JsonNode)new JsonObject
        {
            ["name"] = id,
            ["model"] = id,
            ["modified_at"] = modified,
        }).ToArray());
        return Results.Json(new JsonObject { ["models"] = models });
    }

    public static IResult Version() => Results.Json(new JsonObject { ["version"] = "0.0.0-mock" });

    public async Task WriteStatusErrorAsync(HttpContext http, FaultSpec fault, CancellationToken ct)
    {
        http.Response.StatusCode = fault.Status!.Value;
        if (fault.RetryAfter is not null)
        {
            http.Response.Headers.RetryAfter = fault.RetryAfter;
        }

        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(new JsonObject { ["error"] = $"Mock fault: status {fault.Status}" }.ToJsonString(), ct);
    }

    public async Task WriteCompletionAsync(HttpContext http, MockPlan plan, CancellationToken ct)
    {
        http.Response.ContentType = "application/json";
        if (plan.Fault is { Garbage: true })
        {
            await http.Response.WriteAsync("{\"model\":\"mock\",\"message\":", ct);
            return;
        }

        if (plan.Fault is { InlineErrorCode: not null })
        {
            await http.Response.WriteAsync(new JsonObject { ["error"] = "Mock inline error" }.ToJsonString(), ct);
            return;
        }

        var response = Envelope(plan, plan.Text, done: true);
        await http.Response.WriteAsync(response.ToJsonString(), ct);
    }

    public async Task WriteStreamAsync(HttpContext http, MockPlan plan, CancellationToken ct)
    {
        http.Response.ContentType = "application/x-ndjson";
        Task Send(JsonNode node) => MockPipeline.WriteLineAsync(http, node.ToJsonString() + "\n", ct);

        await http.Response.StartAsync(ct);
        if (plan.Fault is { Garbage: true })
        {
            await MockPipeline.WriteLineAsync(http, "{\"model\":\"mock\",\"message\":\n", ct);
            return;
        }

        var sent = 0;
        foreach (var piece in plan.Chunks)
        {
            if (plan.Fault is { CutAfter: { } cut } && sent >= cut)
            {
                http.Abort();
                return;
            }

            await Send(Envelope(plan, piece, done: false));
            sent++;

            if (plan.Fault is { InlineErrorCode: not null })
            {
                await Send(new JsonObject { ["error"] = "Mock inline error" });
                return;
            }
        }

        if (plan.Fault is { CutAfter: { } finalCut } && sent >= finalCut)
        {
            http.Abort();
            return;
        }

        await Send(Envelope(plan, string.Empty, done: true));
    }

    private JsonObject Envelope(MockPlan plan, string content, bool done)
    {
        var node = new JsonObject
        {
            ["model"] = plan.ModelId,
            ["created_at"] = time.GetUtcNow().ToString("O"),
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
            ["done"] = done,
        };
        if (done)
        {
            node["done_reason"] = plan.FinishReason;
            if (plan.IncludeUsage)
            {
                node["prompt_eval_count"] = plan.InputTokens;
                node["eval_count"] = plan.OutputTokens;
                if (plan.CachedTokens is { } cached)
                {
                    node["prompt_eval_cached_count"] = cached;
                }
            }
        }

        return node;
    }
}
