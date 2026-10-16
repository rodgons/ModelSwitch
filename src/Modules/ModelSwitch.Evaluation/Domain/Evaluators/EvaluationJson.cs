using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModelSwitch.Evaluation.Domain.Evaluators;

/// <summary>JSON helpers shared by the Evaluation domain: camelCase serialization and model-output parsing.</summary>
internal static partial class EvaluationJson
{
    /// <summary>camelCase, compact. Used for <c>score_details</c> and run aggregates.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

    public static JsonElement ToElement(object value) => JsonSerializer.SerializeToElement(value, value.GetType(), Options);

    public static JsonElement String(string value) => JsonSerializer.SerializeToElement(value, Options);

    /// <summary>Compact JSON text of a value; strings as their raw text (the same rule as template rendering).</summary>
    public static string Text(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString()! : JsonSerializer.Serialize(value);

    /// <summary>
    /// Parses model output as one JSON object. The output is trimmed, and a Markdown code fence around the whole output
    /// (<c>```json … ```</c> or <c>``` … ```</c>) is removed, because models add one even when told not to. Nothing else is
    /// forgiven: prose before or after the JSON, several objects, or a non-object value make it unparseable (null).
    /// </summary>
    public static JsonElement? ParseObject(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var text = output.Trim();
        var fence = Fence().Match(text);
        if (fence.Success)
        {
            text = fence.Groups["body"].Value;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\A```(?:json)?[ \t]*\r?\n?(?<body>.*?)\s*```\z", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Fence();
}
