using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Evaluation.Domain.Prompts;

/// <summary>One message of a template as submitted; either value may be missing.</summary>
internal sealed record PromptMessageInput(string? Role, string? Content);

/// <summary>
/// Validates a Prompt Version template (evaluation.md section 2) and returns its stored JSON,
/// <c>{"messages":[{"role":…,"content":…}],"responseFormat":{…}}</c> (<c>responseFormat</c> only when given, in canonical form).
/// Errors are keyed like the body: <c>messages</c>, <c>messages[i].role</c>, <c>messages[i].content</c>, <c>responseFormat</c>,
/// <c>responseFormat.&lt;field&gt;</c>, with 0-based indexes. <c>{{var}}</c> placeholders are not checked here: a variable a
/// Case lacks is that Case's error when a run renders it.
/// </summary>
internal static partial class PromptTemplateRules
{
    public const int MaxMessages = 256;

    public static IReadOnlyList<string> Roles { get; } = ["system", "user", "assistant"];

    public static IReadOnlyList<string> ResponseFormatTypes { get; } = ["text", "json_object", "json_schema"];

    public static Result<string> Validate(IReadOnlyList<PromptMessageInput?>? messages, JsonElement? responseFormat)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (messages is null || messages.Count == 0)
        {
            errors["messages"] = ["At least one message is required."];
        }
        else if (messages.Count > MaxMessages)
        {
            errors["messages"] = [$"A template has at most {MaxMessages} messages."];
        }
        else
        {
            for (var i = 0; i < messages.Count; i++)
            {
                var index = i.ToString(CultureInfo.InvariantCulture);
                var message = messages[i];
                if (message is null)
                {
                    errors[$"messages[{index}]"] = ["The message must be an object with role and content."];
                    continue;
                }

                if (message.Role is null || !Roles.Contains(message.Role, StringComparer.Ordinal))
                {
                    errors[$"messages[{index}].role"] = ["The role must be system, user or assistant."];
                }

                if (string.IsNullOrWhiteSpace(message.Content))
                {
                    errors[$"messages[{index}].content"] = ["The content must not be empty."];
                }
            }
        }

        var format = responseFormat is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } given ? given : (JsonElement?)null;
        if (format is { } value)
        {
            try
            {
                ValidateResponseFormat(value, errors);
            }
            catch (InvalidOperationException)
            {
                errors["responseFormat"] = ["The response format contains a string that is not valid Unicode."];
            }
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The prompt template is invalid.", errors);
        }

        try
        {
            return Serialize(messages!, format);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return Error.Validation("The prompt template is invalid.", new Dictionary<string, string[]> { ["responseFormat"] = ["The response format contains a string that is not valid Unicode."] });
        }
    }

    private static void ValidateResponseFormat(JsonElement format, Dictionary<string, string[]> errors)
    {
        if (format.ValueKind != JsonValueKind.Object)
        {
            errors["responseFormat"] = ["The response format must be an object with a type."];
            return;
        }

        string? type = null;
        if (!format.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String
            || !ResponseFormatTypes.Contains(type = typeElement.GetString(), StringComparer.Ordinal))
        {
            errors["responseFormat.type"] = ["The type must be text, json_object or json_schema."];
            type = null;
        }

        var allowed = type == "json_schema" ? new[] { "type", "schema", "name", "strict" } : ["type"];
        foreach (var property in format.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) && type is not null)
            {
                var field = property.Name.Length <= 64 ? property.Name : property.Name[..64];
                errors[$"responseFormat.{field}"] = [type == "json_schema"
                    ? "Unknown field: a json_schema response format has type, schema, name and strict."
                    : $"Unknown field: a {type} response format has only a type."];
            }
        }

        if (type != "json_schema")
        {
            return;
        }

        if (!format.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Object)
        {
            errors["responseFormat.schema"] = ["A json_schema response format needs its JSON Schema as an object."];
        }

        if (format.TryGetProperty("name", out var name)
            && (name.ValueKind != JsonValueKind.String || !SchemaName().IsMatch(name.GetString() ?? string.Empty)))
        {
            errors["responseFormat.name"] = ["The name must be 1 to 64 letters, digits, '_' or '-'."];
        }

        if (format.TryGetProperty("strict", out var strict) && strict.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            errors["responseFormat.strict"] = ["strict must be true or false."];
        }
    }

    private static string Serialize(IReadOnlyList<PromptMessageInput?> messages, JsonElement? format)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("messages");
            foreach (var message in messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", message!.Role);
                writer.WriteString("content", message.Content);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (format is { } value)
            {
                writer.WritePropertyName("responseFormat");
                CanonicalJson.Write(writer, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SchemaName();
}
