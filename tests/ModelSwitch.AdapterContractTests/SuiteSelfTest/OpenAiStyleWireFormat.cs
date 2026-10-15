using System.Text.Json;
using System.Text.Json.Nodes;
using ModelSwitch.AdapterContractTests.Support;
using ModelSwitch.Adapters.Abstractions;
using Shouldly;

namespace ModelSwitch.AdapterContractTests.SuiteSelfTest;

/// <summary>
/// A synthetic OpenAI-style wire format, used only to prove the suite itself works against a minimal reference Adapter.
/// The real OpenAI-compatible Adapter's tests use recorded fixtures instead (docs/provider-adapters.md section 4).
/// </summary>
public sealed class OpenAiStyleWireFormat : ProviderWireFormat
{
    public override string EndpointPath => "/v1";

    public override string ChatPath => "/v1/chat/completions";

    public override string CheckPath => "/v1/models";

    public override string ListModelsPath => "/v1/models";

    public override string StreamContentType => "text/event-stream";

    public override string EncodeCompletion(CompletionStub stub)
    {
        var body = new JsonObject
        {
            ["id"] = "chatcmpl-1",
            ["object"] = "chat.completion",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = stub.Text },
                ["finish_reason"] = FinishName(stub.FinishReason),
            }),
        };
        if (stub.Usage is { } usage)
        {
            body["usage"] = UsageNode(usage);
        }

        return body.ToJsonString();
    }

    public override IReadOnlyList<string> EncodeStreamDeltas(IReadOnlyList<string> deltas) =>
        [.. deltas.Select(d => Frame(Chunk(new JsonObject { ["content"] = d }, null)))];

    public override IReadOnlyList<string> EncodeStreamEnd(FinishReason finishReason, NeutralUsage? usage)
    {
        var frames = new List<string> { Frame(Chunk([], FinishName(finishReason))) };
        if (usage is { } reported)
        {
            frames.Add(Frame(new JsonObject { ["id"] = "chatcmpl-1", ["choices"] = new JsonArray(), ["usage"] = UsageNode(reported) }));
        }

        frames.Add("data: [DONE]\n\n");
        return frames;
    }

    public override string EncodeError(string? providerErrorCode, string message) =>
        new JsonObject { ["error"] = new JsonObject { ["message"] = message, ["type"] = "error", ["code"] = providerErrorCode } }.ToJsonString();

    public override string EncodeErrorInOkBody(InnerErrorKind kind, string message) => EncodeError(InnerCode(kind), message);

    public override string EncodeErrorInStreamFrame(InnerErrorKind kind, string message) => Frame(JsonNode.Parse(EncodeError(InnerCode(kind), message))!);

    public override string EncodeMalformedBody() => "{\"id\":\"chatcmpl-1\",\"choices\":[{\"message\":";

    public override string EncodeMalformedFrame() => "data: {\"id\":\"chatcmpl-1\",\"choices\":[{\"delta\":\n\n";

    public override string EncodeContextLengthExceeded() => EncodeError("context_length_exceeded", "too long");

    public override string EncodeModelList(IReadOnlyList<string> modelIds) =>
        new JsonObject { ["object"] = "list", ["data"] = new JsonArray(modelIds.Select(id => (JsonNode)new JsonObject { ["id"] = id, ["object"] = "model" }).ToArray()) }
            .ToJsonString();

    public override void AssertJsonObjectRequest(JsonNode requestBody) =>
        requestBody["response_format"]!["type"]!.GetValue<string>().ShouldBe("json_object");

    public override void AssertJsonSchemaRequest(JsonNode requestBody, string name, JsonElement schema, bool strict)
    {
        var format = requestBody["response_format"]!;
        format["type"]!.GetValue<string>().ShouldBe("json_schema");
        format["json_schema"]!["name"]!.GetValue<string>().ShouldBe(name);
        format["json_schema"]!["strict"]!.GetValue<bool>().ShouldBe(strict);
        JsonNode.DeepEquals(format["json_schema"]!["schema"], JsonNode.Parse(schema.GetRawText())).ShouldBeTrue();
    }

    private static string InnerCode(InnerErrorKind kind) => kind switch
    {
        InnerErrorKind.RateLimit => "rate_limit_exceeded",
        InnerErrorKind.Quota => "insufficient_quota",
        _ => "unknown_error",
    };

    private static string FinishName(FinishReason reason) => reason switch
    {
        FinishReason.Length => "length",
        FinishReason.ContentFilter => "content_filter",
        _ => "stop",
    };

    private static JsonObject Chunk(JsonObject delta, string? finish) => new()
    {
        ["id"] = "chatcmpl-1",
        ["object"] = "chat.completion.chunk",
        ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish }),
    };

    private static string Frame(JsonNode node) => $"data: {node.ToJsonString()}\n\n";

    private static JsonObject UsageNode(NeutralUsage usage)
    {
        var node = new JsonObject
        {
            ["prompt_tokens"] = usage.InputTokens,
            ["completion_tokens"] = usage.OutputTokens,
            ["total_tokens"] = usage.InputTokens + usage.OutputTokens,
        };
        if (usage.CachedInputTokens is { } cached)
        {
            node["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = cached };
        }

        return node;
    }
}
