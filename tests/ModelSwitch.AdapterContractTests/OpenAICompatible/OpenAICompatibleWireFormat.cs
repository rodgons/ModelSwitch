using System.Text.Json;
using System.Text.Json.Nodes;
using ModelSwitch.AdapterContractTests.Support;
using ModelSwitch.Adapters.Abstractions;
using Shouldly;

namespace ModelSwitch.AdapterContractTests.OpenAICompatible;

/// <summary>
/// The OpenAI wire format as the contract suite puts it on the wire. Every body is rendered from a fixture used as a template, so the
/// field set (OpenRouter's <c>provider</c>, <c>native_finish_reason</c>, <c>cost</c>...) is the captured one and only the values the
/// scenario cares about are replaced. The success and error templates are the recorded fixtures; the 200-with-error and mid-stream
/// error templates are synthetic, because they cannot be provoked on demand.
/// </summary>
public sealed class OpenAICompatibleWireFormat : ProviderWireFormat
{
    private readonly JsonNode _completion = OpenAiFixtures.Load("completion-text.json").BodyNode;
    private readonly JsonNode _error = OpenAiFixtures.Load("synthetic-openai-400.json").BodyNode;
    private readonly JsonNode _errorInOkBody = OpenAiFixtures.Load("synthetic-200-error-body.json").BodyNode;
    private readonly IReadOnlyList<JsonObject> _streamChunks = OpenAiFixtures.Chunks("stream-text.sse");
    private readonly IReadOnlyList<JsonObject> _errorChunks = OpenAiFixtures.Chunks("synthetic-stream-midstream-error.sse");

    public override string EndpointPath => "/api/v1";

    public override string ChatPath => "/api/v1/chat/completions";

    public override string CheckPath => "/api/v1/models";

    public override string ListModelsPath => "/api/v1/models";

    public override string StreamContentType => "text/event-stream";

    public override string EncodeCompletion(CompletionStub stub)
    {
        var body = _completion.DeepClone();
        var choice = body["choices"]![0]!;
        choice["message"]!["content"] = stub.Text;
        choice["finish_reason"] = FinishName(stub.FinishReason);
        choice["native_finish_reason"] = FinishName(stub.FinishReason);
        ApplyUsage(body.AsObject(), stub.Usage);
        return body.ToJsonString();
    }

    public override IReadOnlyList<string> EncodeStreamDeltas(IReadOnlyList<string> deltas)
    {
        var frames = new List<string> { Frame(RoleChunk()) };
        foreach (var delta in deltas)
        {
            var chunk = ContentChunk();
            chunk["choices"]![0]!["delta"]!["content"] = delta;
            frames.Add(Frame(chunk));
        }

        return frames;
    }

    public override IReadOnlyList<string> EncodeStreamEnd(FinishReason finishReason, NeutralUsage? usage)
    {
        var finish = FinishChunk();
        finish["choices"]![0]!["finish_reason"] = FinishName(finishReason);
        finish["choices"]![0]!["native_finish_reason"] = FinishName(finishReason);
        var frames = new List<string> { Frame(finish) };
        if (usage is not null)
        {
            var usageChunk = UsageChunk();
            ApplyUsage(usageChunk, usage);
            frames.Add(Frame(usageChunk));
        }

        frames.Add("data: [DONE]\n\n");
        return frames;
    }

    public override string EncodeError(string? providerErrorCode, string message)
    {
        var body = _error.DeepClone();
        body["error"]!["code"] = providerErrorCode;
        body["error"]!["message"] = message;
        return body.ToJsonString();
    }

    public override string EncodeErrorInOkBody(InnerErrorKind kind, string message)
    {
        // OpenRouter answers 200 with a numeric status in the error's code.
        var body = _errorInOkBody.DeepClone();
        body["error"]!["code"] = kind switch
        {
            InnerErrorKind.RateLimit => 429,
            InnerErrorKind.Quota => 402,
            _ => 500,
        };
        body["error"]!["message"] = message;
        return body.ToJsonString();
    }

    public override string EncodeErrorInStreamFrame(InnerErrorKind kind, string message)
    {
        var chunk = _errorChunks.Single(c => c["error"] is not null).DeepClone();
        chunk["error"]!["code"] = kind switch
        {
            InnerErrorKind.RateLimit => "rate_limit_exceeded",
            InnerErrorKind.Quota => "insufficient_quota",
            _ => "server_error",
        };
        chunk["error"]!["message"] = message;
        return Frame(chunk);
    }

    public override string EncodeMalformedBody() => EncodeCompletion(new CompletionStub("x", FinishReason.Stop, null))[..60];

    public override string EncodeMalformedFrame() => "data: {\"id\":\"gen-1\",\"choices\":[{\"delta\":\n\n";

    public override string EncodeContextLengthExceeded() => OpenAiFixtures.Load("synthetic-openai-400-context-length.json").Body;

    public override string EncodeModelList(IReadOnlyList<string> modelIds) =>
        new JsonObject
        {
            ["data"] = new JsonArray(modelIds.Select(id => (JsonNode)new JsonObject { ["id"] = id, ["name"] = id, ["context_length"] = 128000 }).ToArray()),
        }.ToJsonString();

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

    private static void ApplyUsage(JsonObject holder, NeutralUsage? usage)
    {
        if (usage is null)
        {
            holder.Remove("usage");
            return;
        }

        var node = holder["usage"]?.AsObject() ?? [];
        node["prompt_tokens"] = usage.InputTokens;
        node["completion_tokens"] = usage.OutputTokens;
        node["total_tokens"] = usage.InputTokens + usage.OutputTokens;
        var details = node["prompt_tokens_details"]?.AsObject() ?? [];
        if (usage.CachedInputTokens is { } cached)
        {
            details["cached_tokens"] = cached;
            node["prompt_tokens_details"] = details;
        }
        else
        {
            node.Remove("prompt_tokens_details");
        }

        holder["usage"] = node;
    }

    private JsonObject RoleChunk() => (JsonObject)_streamChunks[0].DeepClone();

    private JsonObject ContentChunk() => (JsonObject)_streamChunks.First(c => c["choices"]!.AsArray().Count > 0 && !string.IsNullOrEmpty(c["choices"]![0]!["delta"]!["content"]!.GetValue<string>())).DeepClone();

    private JsonObject FinishChunk() => (JsonObject)_streamChunks.First(c => c["choices"]!.AsArray().Count > 0 && c["choices"]![0]!["finish_reason"] is not null).DeepClone();

    private JsonObject UsageChunk() => (JsonObject)_streamChunks.First(c => c["usage"] is not null).DeepClone();

    private static string FinishName(FinishReason reason) => reason switch
    {
        FinishReason.Length => "length",
        FinishReason.ContentFilter => "content_filter",
        _ => "stop",
    };

    private static string Frame(JsonNode node) => $"data: {node.ToJsonString()}\n\n";
}
