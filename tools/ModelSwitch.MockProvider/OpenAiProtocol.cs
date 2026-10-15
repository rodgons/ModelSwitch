using System.Text.Json;
using System.Text.Json.Nodes;

namespace ModelSwitch.MockProvider;

/// <summary>The OpenAI-compatible wire protocol: <c>/v1/chat/completions</c> (JSON or SSE) and <c>/v1/models</c>.</summary>
internal sealed class OpenAiProtocol(TimeProvider time) : IMockProtocol
{
    private static long _sequence;

    public static readonly string[] ModelIds = ["mock-echo", "mock-fixed", "mock-a", "mock-b", "mock-c"];

    public static async Task<IResult> ChatAsync(HttpContext http, ScenarioStore store, TimeProvider time)
    {
        JsonNode? body;
        try
        {
            body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        }
        catch (JsonException)
        {
            return Results.BadRequest(ErrorBody("The request body is not valid JSON.", null));
        }

        if (body is not JsonObject request)
        {
            return Results.BadRequest(ErrorBody("The request body must be a JSON object.", null));
        }

        var messages = (request["messages"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(m => new MockMessage(m["role"]?.GetValue<string>() ?? "user", ContentText(m["content"])))
            .ToList();
        var stream = request["stream"]?.GetValue<bool>() ?? false;
        var usageRequested = request["stream_options"]?["include_usage"]?.GetValue<bool>() ?? false;
        var maxTokens = (request["max_completion_tokens"] ?? request["max_tokens"])?.GetValue<int>();

        if (!PlanResolver.TryResolve(http, store, request["model"]?.GetValue<string>(), messages, maxTokens, stream, !stream || usageRequested, out var plan, out var error))
        {
            return Results.BadRequest(ErrorBody(error!, null));
        }

        await MockPipeline.RunAsync(http, plan!, new OpenAiProtocol(time), time);
        return Results.Empty;
    }

    public static IResult Models(TimeProvider time)
    {
        var created = time.GetUtcNow().ToUnixTimeSeconds();
        var data = new JsonArray(ModelIds.Select(id => (JsonNode)new JsonObject
        {
            ["id"] = id,
            ["object"] = "model",
            ["created"] = created,
            ["owned_by"] = "mock",
        }).ToArray());
        return Results.Json(new JsonObject { ["object"] = "list", ["data"] = data });
    }

    public async Task WriteStatusErrorAsync(HttpContext http, FaultSpec fault, CancellationToken ct)
    {
        http.Response.StatusCode = fault.Status!.Value;
        if (fault.RetryAfter is not null)
        {
            http.Response.Headers.RetryAfter = fault.RetryAfter;
        }

        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(ErrorBody($"Mock fault: status {fault.Status}", fault.ErrorCode).ToJsonString(), ct);
    }

    public async Task WriteCompletionAsync(HttpContext http, MockPlan plan, CancellationToken ct)
    {
        http.Response.ContentType = "application/json";
        if (plan.Fault is { Garbage: true })
        {
            await http.Response.WriteAsync("{\"id\":\"chatcmpl-mock\",\"choices\":[{\"message\":", ct);
            return;
        }

        if (plan.Fault is { InlineErrorCode: { } code })
        {
            await http.Response.WriteAsync(ErrorBody("Mock inline error", code).ToJsonString(), ct);
            return;
        }

        var response = new JsonObject
        {
            ["id"] = NextId(),
            ["object"] = "chat.completion",
            ["created"] = time.GetUtcNow().ToUnixTimeSeconds(),
            ["model"] = plan.ModelId,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = plan.Text },
                ["finish_reason"] = plan.FinishReason,
            }),
        };
        if (plan.IncludeUsage)
        {
            response["usage"] = UsageNode(plan);
        }

        await http.Response.WriteAsync(response.ToJsonString(), ct);
    }

    public async Task WriteStreamAsync(HttpContext http, MockPlan plan, CancellationToken ct)
    {
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        var id = NextId();
        var created = time.GetUtcNow().ToUnixTimeSeconds();

        JsonObject Chunk(JsonObject delta, string? finish) => new()
        {
            ["id"] = id,
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["model"] = plan.ModelId,
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish }),
        };

        Task Send(JsonNode node) => MockPipeline.WriteLineAsync(http, $"data: {node.ToJsonString()}\n\n", ct);

        await http.Response.StartAsync(ct);
        if (plan.Fault is { Garbage: true })
        {
            await MockPipeline.WriteLineAsync(http, "data: {\"id\":\"chatcmpl-mock\",\"choices\":[{\"delta\":\n\n", ct);
            return;
        }

        await Send(Chunk(new JsonObject { ["role"] = "assistant", ["content"] = string.Empty }, null));
        var sent = 0;
        foreach (var piece in plan.Chunks)
        {
            if (plan.Fault is { CutAfter: { } cut } && sent >= cut)
            {
                await MockPipeline.CutConnectionAsync(http, ct);
                return;
            }

            if (plan.Fault is { ChunkDelay: { } pause })
            {
                await Task.Delay(pause, time, ct);
            }

            await Send(Chunk(new JsonObject { ["content"] = piece }, null));
            sent++;

            if (plan.Fault is { InlineErrorCode: { } code })
            {
                await Send(ErrorBody("Mock inline error", code));
                return;
            }
        }

        if (plan.Fault is { CutAfter: { } finalCut } && sent >= finalCut)
        {
            await MockPipeline.CutConnectionAsync(http, ct);
            return;
        }

        await Send(Chunk([], plan.FinishReason));
        if (plan.IncludeUsage)
        {
            await Send(new JsonObject
            {
                ["id"] = id,
                ["object"] = "chat.completion.chunk",
                ["created"] = created,
                ["model"] = plan.ModelId,
                ["choices"] = new JsonArray(),
                ["usage"] = UsageNode(plan),
            });
        }

        await MockPipeline.WriteLineAsync(http, "data: [DONE]\n\n", ct);
    }

    internal static JsonObject ErrorBody(string message, string? code) => new()
    {
        ["error"] = new JsonObject
        {
            ["message"] = message,
            ["type"] = "mock_error",
            ["param"] = null,
            ["code"] = code,
        },
    };

    private static JsonObject UsageNode(MockPlan plan)
    {
        var usage = new JsonObject
        {
            ["prompt_tokens"] = plan.InputTokens,
            ["completion_tokens"] = plan.OutputTokens,
            ["total_tokens"] = plan.InputTokens + plan.OutputTokens,
        };
        if (plan.CachedTokens is { } cached)
        {
            usage["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = cached };
        }

        return usage;
    }

    private static string NextId() => $"chatcmpl-mock-{Interlocked.Increment(ref _sequence)}";

    private static string ContentText(JsonNode? content) => content switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray parts => string.Join(' ', parts.OfType<JsonObject>().Select(p => p["text"]?.GetValue<string>()).Where(t => t is not null)),
        _ => string.Empty,
    };
}
