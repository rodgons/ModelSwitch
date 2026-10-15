using System.Text.Json;
using System.Text.Json.Nodes;
using ModelSwitch.AdapterContractTests.Support;
using ModelSwitch.Adapters.Abstractions;
using Shouldly;

namespace ModelSwitch.AdapterContractTests.Ollama;

/// <summary>
/// Ollama's native wire format (<c>/api/chat</c>, NDJSON streaming, <c>/api/tags</c>, <c>/api/version</c>), derived from the hand-authored
/// fixtures in <c>fixtures/ollama</c>. Each fixture carries <c>"_source": "synthetic"</c> (Ollama has no recording requirement, docs/provider-adapters.md
/// section 4); the marker is stripped before a fixture goes on the wire.
/// </summary>
public sealed class OllamaWireFormat : ProviderWireFormat
{
    public override string ChatPath => "/api/chat";

    public override string CheckPath => "/api/version";

    public override string ListModelsPath => "/api/tags";

    public override string StreamContentType => "application/x-ndjson";

    // Ollama has no content-filter finish reason. The Adapter maps a "content_filter" done_reason (as a proxy in front of it may report) so the suite can exercise it.
    public override bool InnerErrorsAreClassifiable => false;

    public override string EncodeCheckBody() => Strip(Fixture("version.json")).ToJsonString();

    public override string EncodeCompletion(CompletionStub stub)
    {
        var body = Strip(Fixture("chat-completion.json"));
        body["message"]!["content"] = stub.Text;
        ApplyEnd(body, stub.FinishReason, stub.Usage);
        return body.ToJsonString();
    }

    public override IReadOnlyList<string> EncodeStreamDeltas(IReadOnlyList<string> deltas)
    {
        var template = Lines("chat-stream.ndjson")[0];
        return [.. deltas.Select(d =>
        {
            var line = Strip(JsonNode.Parse(template)!);
            line["message"]!["content"] = d;
            return line.ToJsonString() + "\n";
        })];
    }

    public override IReadOnlyList<string> EncodeStreamEnd(FinishReason finishReason, NeutralUsage? usage)
    {
        var line = Strip(JsonNode.Parse(Lines("chat-stream.ndjson")[^1])!);
        ApplyEnd(line, finishReason, usage);
        return [line.ToJsonString() + "\n"];
    }

    public override string EncodeError(string? providerErrorCode, string message) =>
        providerErrorCode is null
            ? new JsonObject { ["error"] = message }.ToJsonString()
            : new JsonObject { ["error"] = new JsonObject { ["message"] = message, ["code"] = providerErrorCode } }.ToJsonString();

    public override string EncodeErrorInOkBody(InnerErrorKind kind, string message)
    {
        var body = Strip(Fixture("error.json"));
        body["error"] = message;
        return body.ToJsonString();
    }

    public override string EncodeErrorInStreamFrame(InnerErrorKind kind, string message)
    {
        var line = Strip(JsonNode.Parse(Lines("error-in-stream.ndjson")[0])!);
        line["error"] = message;
        return line.ToJsonString() + "\n";
    }

    public override string EncodeMalformedBody() => EncodeCompletion(new CompletionStub("x", FinishReason.Stop, null))[..40];

    public override string EncodeMalformedFrame() => "{\"model\":\"llama3.2\",\"message\":{\"role\":\"assistant\",\"content\":\n";

    public override void AssertJsonObjectRequest(JsonNode requestBody) =>
        requestBody["format"]!.GetValue<string>().ShouldBe("json");

    public override void AssertJsonSchemaRequest(JsonNode requestBody, string name, JsonElement schema, bool strict) =>
        JsonNode.DeepEquals(requestBody["format"], JsonNode.Parse(schema.GetRawText())).ShouldBeTrue("Ollama takes the JSON schema itself as `format`");

    public override string EncodeModelList(IReadOnlyList<string> modelIds)
    {
        var body = Strip(Fixture("tags.json"));
        var template = body["models"]![0]!;
        var models = new JsonArray();
        foreach (var id in modelIds)
        {
            var model = template.DeepClone();
            model["name"] = id;
            model["model"] = id;
            models.Add(model);
        }

        body["models"] = models;
        return body.ToJsonString();
    }

    private static void ApplyEnd(JsonNode body, FinishReason reason, NeutralUsage? usage)
    {
        body["done"] = true;
        body["done_reason"] = reason switch
        {
            FinishReason.Length => "length",
            FinishReason.ContentFilter => "content_filter",
            _ => "stop",
        };
        body.AsObject().Remove("prompt_eval_count");
        body.AsObject().Remove("eval_count");
        if (usage is { } reported)
        {
            body["prompt_eval_count"] = reported.InputTokens;
            body["eval_count"] = reported.OutputTokens;
            if (reported.CachedInputTokens is { } cached)
            {
                body["prompt_eval_cached_count"] = cached;
            }
        }
    }

    private static JsonNode Strip(JsonNode node)
    {
        var obj = node.AsObject();
        obj.Remove("_source");
        obj.Remove("_note");
        return node;
    }

    private static JsonNode Fixture(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "ollama", name)))!;

    private static string[] Lines(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "ollama", name)).Where(l => l.Length > 0).ToArray();
}
