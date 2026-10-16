using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ModelSwitch.Evaluation.Domain;

/// <summary>
/// One fixed text form for a JSON value, so equal content always hashes the same (evaluation.md section 1, <c>content_sha256</c>).
/// Object members are sorted by name in ordinal (UTF-16 code unit) order, as RFC 8785 does; there is no insignificant whitespace;
/// strings are re-encoded from their decoded value with minimal escaping, so <c>"A"</c> and <c>"A"</c> are the same;
/// numbers keep their literal text (<c>1.0</c> and <c>1</c> differ). The input must not repeat a member name.
/// </summary>
internal static class CanonicalJson
{
    private static readonly JsonWriterOptions _options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = false,
    };

    /// <summary>The canonical text of <paramref name="value"/>.</summary>
    /// <exception cref="InvalidOperationException">A string holds an unpaired surrogate and has no UTF-8 form.</exception>
    public static string Serialize(JsonElement value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, _options))
        {
            Write(writer, value);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>Writes the canonical form of <paramref name="value"/> to <paramref name="writer"/>.</summary>
    public static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentException("The value is not a JSON value.", nameof(value));
        }
    }

    /// <summary>The <c>JsonDocument</c> options every evaluation upload is parsed with: depth 32 and no repeated member names.</summary>
    public static JsonDocumentOptions DocumentOptions { get; } = new()
    {
        MaxDepth = 32,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowDuplicateProperties = false,
    };
}
