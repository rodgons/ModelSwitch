using System.Text.Json;
using ModelSwitch.Routing.Contracts;

namespace ModelSwitch.Configuration.Application;

/// <summary>Reads the <c>registry.provider_model.capabilities</c> document (data-model.md). Missing fields mean "not declared".</summary>
internal static class CapabilitiesParser
{
    public static ModelCapabilities Parse(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        var parameters = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("parameters", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String))
            {
                parameters.Add(item.GetString()!);
            }
        }

        return new ModelCapabilities(
            Bool(root, "streaming"),
            Bool(root, "jsonObject"),
            Bool(root, "jsonSchema"),
            Int(root, "contextWindow"),
            Int(root, "maxOutputTokens"),
            parameters,
            root.TryGetProperty("tokenizer", out var tokenizer) && tokenizer.ValueKind == JsonValueKind.String ? tokenizer.GetString() : null);
    }

    private static ModelCapabilities Empty => new(false, false, false, 0, 0, new HashSet<string>(), null);

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
}
