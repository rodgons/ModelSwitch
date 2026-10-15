using System.Text.Json;
using System.Text.Json.Nodes;

namespace ModelSwitch.AdapterContractTests.OpenAICompatible;

/// <summary>
/// One wire fixture from <c>fixtures/openai-compatible/</c>. <c>.json</c> files are envelopes (<c>status</c>, <c>headers</c> and a
/// <c>body</c> or <c>body_text</c>); <c>.sse</c> files are raw streams whose leading SSE comment lines carry the stamp.
/// <see cref="Source"/> is <c>recorded-openrouter</c>, <c>provisional</c> (hand-written until the recording script is run) or <c>synthetic</c>.
/// </summary>
public sealed record OpenAiFixture(string Name, string Source, int Status, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string ContentType => Headers.TryGetValue("content-type", out var type) ? type : "application/json";

    public JsonNode BodyNode => JsonNode.Parse(Body)!;

    public override string ToString() => Name;
}

public static class OpenAiFixtures
{
    private static readonly string _directory = Path.Combine(AppContext.BaseDirectory, "fixtures", "openai-compatible");

    public static IEnumerable<string> Names => Directory.GetFiles(_directory).Select(Path.GetFileName).Order(StringComparer.Ordinal)!;

    public static OpenAiFixture Load(string name)
    {
        var text = File.ReadAllText(Path.Combine(_directory, name));
        if (name.EndsWith(".sse", StringComparison.Ordinal))
        {
            return new OpenAiFixture(name, StampOf(text), 200, new Dictionary<string, string> { ["content-type"] = "text/event-stream" }, text);
        }

        var envelope = JsonNode.Parse(text)!.AsObject();
        var headers = envelope["headers"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
        var body = envelope["body"]?.ToJsonString() ?? envelope["body_text"]!.GetValue<string>();
        return new OpenAiFixture(name, envelope["_source"]!.GetValue<string>(), envelope["status"]!.GetValue<int>(), headers, body);
    }

    /// <summary>The data frames of a stream fixture, as JSON, excluding comments and <c>[DONE]</c>.</summary>
    public static IReadOnlyList<JsonObject> Chunks(string name)
    {
        var chunks = new List<JsonObject>();
        foreach (var line in File.ReadAllLines(Path.Combine(_directory, name)))
        {
            if (line.StartsWith("data: {", StringComparison.Ordinal))
            {
                chunks.Add(JsonNode.Parse(line[6..])!.AsObject());
            }
        }

        return chunks;
    }

    private static string StampOf(string sse)
    {
        const string prefix = ": _source: ";
        foreach (var line in sse.Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..].Trim();
            }
        }

        throw new JsonException("The stream fixture has no ': _source:' stamp.");
    }
}
