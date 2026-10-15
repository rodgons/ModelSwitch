using System.Text;
using System.Text.Json;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Inference.Application;

/// <summary>A validated <c>POST /v1/chat/completions</c> body.</summary>
/// <param name="Alias">The Model Alias.</param>
/// <param name="Chat">The provider-neutral request.</param>
/// <param name="Stream"><c>stream: true</c>.</param>
/// <param name="IncludeUsage"><c>stream_options.include_usage</c>: whether the client wants the final usage chunk. The gateway asks the Provider for usage either way.</param>
internal sealed record ParsedChatRequest(string Alias, NeutralChatRequest Chat, bool Stream = false, bool IncludeUsage = false);

/// <summary>
/// Hand-written validation of the chat completions body (docs/api.md section 1; no reflection-heavy validation on the
/// hot path). It collects every problem so the client can fix them in one round trip. Unsupported parameters are
/// rejected, never ignored (FR-02). Error messages name the field and never echo request content (ADR-0010).
/// </summary>
internal static class ChatRequestParser
{
    private const int _maxStopSequences = 4;

    // Recognised OpenAI parameters the gateway does not support (api.md section 1), rejected when present and not null.
    private static readonly HashSet<string> _unsupported = new(StringComparer.Ordinal)
    {
        "tools", "tool_choice", "functions", "function_call", "parallel_tool_calls",
        "logprobs", "top_logprobs", "audio", "modalities", "prediction",
    };

    public static Result<ParsedChatRequest> Parse(JsonElement root, InferenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var problems = new Problems();
        if (root.ValueKind != JsonValueKind.Object)
        {
            problems.Invalid("body", "The request body must be a JSON object.");
            return problems.ToError();
        }

        string? alias = null;
        var stream = false;
        var includeUsage = false;
        IReadOnlyList<NeutralMessage>? messages = null;
        int? maxTokens = null;
        int? maxCompletionTokens = null;
        double? temperature = null;
        double? topP = null;
        double? presence = null;
        double? frequency = null;
        long? seed = null;
        IReadOnlyList<string>? stop = null;
        ResponseFormat format = new ResponseFormat.Text();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in root.EnumerateObject())
        {
            var name = property.Name;
            var value = property.Value;
            seen.Add(name);
            switch (name)
            {
                case "model":
                    alias = ReadAlias(value, problems);
                    break;
                case "messages":
                    messages = ReadMessages(value, options.MaxMessages, problems);
                    break;
                case "stream":
                    stream = ReadStream(value, problems);
                    break;
                case "stream_options":
                    includeUsage = ReadStreamOptions(value, problems);
                    break;
                case "max_tokens":
                    maxTokens = ReadInt(name, value, 1, int.MaxValue, problems);
                    break;
                case "max_completion_tokens":
                    maxCompletionTokens = ReadInt(name, value, 1, int.MaxValue, problems);
                    break;
                case "temperature":
                    temperature = ReadNumber(name, value, 0, 2, problems);
                    break;
                case "top_p":
                    topP = ReadNumber(name, value, 0, 1, problems);
                    break;
                case "presence_penalty":
                    presence = ReadNumber(name, value, -2, 2, problems);
                    break;
                case "frequency_penalty":
                    frequency = ReadNumber(name, value, -2, 2, problems);
                    break;
                case "seed":
                    seed = ReadSeed(value, problems);
                    break;
                case "stop":
                    stop = ReadStop(value, problems);
                    break;
                case "response_format":
                    format = ReadResponseFormat(value, problems);
                    break;
                case "n":
                    ReadN(value, problems);
                    break;
                case "user":
                    if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    {
                        problems.Invalid(name, "'user' must be a string.");
                    }

                    break; // accepted, never stored: it may be personal data
                default:
                    if (_unsupported.Contains(name))
                    {
                        // logprobs:false is the OpenAI default and means "not requested".
                        if (value.ValueKind is not JsonValueKind.Null && !(name == "logprobs" && value.ValueKind == JsonValueKind.False))
                        {
                            problems.Unsupported(name, $"'{name}' is not supported.");
                        }
                    }
                    else
                    {
                        problems.Unsupported(name, "Unknown parameter.");
                    }

                    break;
            }
        }

        if (!seen.Contains("model"))
        {
            problems.Invalid("model", "'model' is required.");
        }

        if (!seen.Contains("messages"))
        {
            problems.Invalid("messages", "'messages' is required.");
        }

        if (maxTokens is not null && maxCompletionTokens is not null && maxTokens != maxCompletionTokens)
        {
            problems.Invalid("max_tokens", "'max_tokens' and 'max_completion_tokens' must be equal when both are set.");
        }

        if (problems.Any)
        {
            return problems.ToError();
        }

        return new ParsedChatRequest(alias!, new NeutralChatRequest(messages!)
        {
            MaxOutputTokens = maxCompletionTokens ?? maxTokens,
            Temperature = temperature,
            TopP = topP,
            PresencePenalty = presence,
            FrequencyPenalty = frequency,
            Stop = stop,
            Seed = seed,
            ResponseFormat = format,
        }, stream, stream && includeUsage);
    }

    private static string? ReadAlias(JsonElement value, Problems problems)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            problems.Invalid("model", "'model' must be a non-empty string.");
            return null;
        }

        return value.GetString();
    }

    private static List<NeutralMessage>? ReadMessages(JsonElement value, int maxMessages, Problems problems)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            problems.Invalid("messages", "'messages' must be an array.");
            return null;
        }

        var count = value.GetArrayLength();
        if (count == 0)
        {
            problems.Invalid("messages", "'messages' must contain at least one message.");
            return null;
        }

        if (count > maxMessages)
        {
            problems.Invalid("messages", $"'messages' must contain at most {maxMessages} messages.");
            return null;
        }

        var result = new List<NeutralMessage>(count);
        var index = 0;
        foreach (var message in value.EnumerateArray())
        {
            var parsed = ReadMessage(message, $"messages[{index}]", problems);
            if (parsed is not null)
            {
                result.Add(parsed);
            }

            index++;
        }

        return result;
    }

    private static NeutralMessage? ReadMessage(JsonElement message, string path, Problems problems)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            problems.Invalid(path, "A message must be an object.");
            return null;
        }

        MessageRole? role = null;
        string? text = null;
        var hasContent = false;
        foreach (var property in message.EnumerateObject())
        {
            switch (property.Name)
            {
                case "role":
                    role = ReadRole(property.Value, $"{path}.role", problems);
                    break;
                case "content":
                    hasContent = true;
                    text = ReadContent(property.Value, $"{path}.content", problems);
                    break;
                default:
                    problems.Unsupported($"{path}.{property.Name}", $"'{property.Name}' is not supported on a message.");
                    break;
            }
        }

        if (role is null && !message.TryGetProperty("role", out _))
        {
            problems.Invalid($"{path}.role", "'role' is required.");
        }

        if (!hasContent)
        {
            problems.Invalid($"{path}.content", "'content' is required.");
        }

        return role is not null && text is not null ? new NeutralMessage(role.Value, text) : null;
    }

    private static MessageRole? ReadRole(JsonElement value, string path, Problems problems)
    {
        var role = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        switch (role)
        {
            case "system":
            case "developer":
                return MessageRole.System;
            case "user":
                return MessageRole.User;
            case "assistant":
                return MessageRole.Assistant;
            case "tool":
            case "function":
                problems.Unsupported(path, $"The '{role}' role is not supported.");
                return null;
            default:
                problems.Invalid(path, "'role' must be one of system, developer, user or assistant.");
                return null;
        }
    }

    // A string, or an array of {type:"text"} parts, which are concatenated in order.
    private static string? ReadContent(JsonElement value, string path, Problems problems)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            problems.Invalid(path, "'content' must be a string or an array of text parts.");
            return null;
        }

        var builder = new StringBuilder();
        var failed = false;
        var index = 0;
        foreach (var part in value.EnumerateArray())
        {
            var partPath = $"{path}[{index++}]";
            if (part.ValueKind != JsonValueKind.Object)
            {
                problems.Invalid(partPath, "A content part must be an object.");
                failed = true;
                continue;
            }

            if (!part.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                problems.Invalid($"{partPath}.type", "'type' is required.");
                failed = true;
            }
            else if (type.GetString() != "text")
            {
                problems.Unsupported($"{partPath}.type", "Only text content parts are supported.");
                failed = true;
            }
            else if (!part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            {
                problems.Invalid($"{partPath}.text", "'text' must be a string.");
                failed = true;
            }
            else
            {
                builder.Append(text.GetString());
            }
        }

        return failed ? null : builder.ToString();
    }

    private static bool ReadStream(JsonElement value, Problems problems)
    {
        if (value.ValueKind is JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
        {
            problems.Invalid("stream", "'stream' must be a boolean.");
        }

        return false;
    }

    private static bool ReadStreamOptions(JsonElement value, Problems problems)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            problems.Invalid("stream_options", "'stream_options' must be an object.");
            return false;
        }

        var includeUsage = false;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name != "include_usage")
            {
                problems.Unsupported($"stream_options.{property.Name}", "Unknown parameter.");
            }
            else if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                problems.Invalid("stream_options.include_usage", "'include_usage' must be a boolean.");
            }
            else
            {
                includeUsage = property.Value.ValueKind == JsonValueKind.True;
            }
        }

        return includeUsage;
    }

    private static int? ReadInt(string name, JsonElement value, int min, int max, Problems problems)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < min || number > max)
        {
            problems.Invalid(name, $"'{name}' must be an integer of at least {min}.");
            return null;
        }

        return (int)number;
    }

    private static double? ReadNumber(string name, JsonElement value, double min, double max, Problems problems)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number < min || number > max)
        {
            problems.Invalid(name, $"'{name}' must be a number between {min:0.#} and {max:0.#}.");
            return null;
        }

        return number;
    }

    private static long? ReadSeed(JsonElement value, Problems problems)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var seed))
        {
            problems.Invalid("seed", "'seed' must be an integer.");
            return null;
        }

        return seed;
    }

    private static List<string>? ReadStop(JsonElement value, Problems problems)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.String when value.GetString() is { Length: > 0 } single:
                return [single];
            case JsonValueKind.Array:
                var sequences = new List<string>();
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(item.GetString()))
                    {
                        problems.Invalid("stop", "'stop' must contain only non-empty strings.");
                        return null;
                    }

                    sequences.Add(item.GetString()!);
                }

                if (sequences.Count is 0 or > _maxStopSequences)
                {
                    problems.Invalid("stop", $"'stop' must contain between 1 and {_maxStopSequences} sequences.");
                    return null;
                }

                return sequences;
            default:
                problems.Invalid("stop", "'stop' must be a non-empty string or an array of up to 4 non-empty strings.");
                return null;
        }
    }

    private static ResponseFormat ReadResponseFormat(JsonElement value, Problems problems)
    {
        ResponseFormat fallback = new ResponseFormat.Text();
        if (value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
        {
            problems.Invalid("response_format", "'response_format' must be an object with a 'type'.");
            return fallback;
        }

        switch (type.GetString())
        {
            case "text":
                return fallback;
            case "json_object":
                return new ResponseFormat.JsonObject();
            case "json_schema":
                if (!value.TryGetProperty("json_schema", out var schema) || schema.ValueKind != JsonValueKind.Object)
                {
                    problems.Invalid("response_format.json_schema", "'json_schema' must be an object.");
                    return fallback;
                }

                if (!schema.TryGetProperty("name", out var schemaName) || schemaName.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(schemaName.GetString()))
                {
                    problems.Invalid("response_format.json_schema.name", "'name' must be a non-empty string.");
                    return fallback;
                }

                if (!schema.TryGetProperty("schema", out var body) || body.ValueKind != JsonValueKind.Object)
                {
                    problems.Invalid("response_format.json_schema.schema", "'schema' must be an object.");
                    return fallback;
                }

                var strict = schema.TryGetProperty("strict", out var strictValue) && strictValue.ValueKind == JsonValueKind.True;
                return new ResponseFormat.JsonSchema(schemaName.GetString()!, body.Clone(), strict);
            default:
                problems.Invalid("response_format.type", "'type' must be text, json_object or json_schema.");
                return fallback;
        }
    }

    private static void ReadN(JsonElement value, Problems problems)
    {
        if (value.ValueKind == JsonValueKind.Null || (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n == 1))
        {
            return;
        }

        problems.Unsupported("n", "Only n = 1 is supported.");
    }

    /// <summary>Field problems in the order found. Any unsupported parameter makes the whole error <c>unsupported_parameter</c>.</summary>
    private sealed class Problems
    {
        private readonly Dictionary<string, List<string>> _fields = new(StringComparer.Ordinal);
        private bool _unsupportedSeen;

        public bool Any => _fields.Count > 0;

        public void Invalid(string field, string message) => Add(field, message);

        public void Unsupported(string field, string message)
        {
            _unsupportedSeen = true;
            Add(field, message);
        }

        public Error ToError()
        {
            var fields = _fields.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
            return _unsupportedSeen
                ? new Error(ErrorCode.UnsupportedParameter, "The request uses a parameter that is not supported.", fields)
                : new Error(ErrorCode.InvalidRequest, "The request is invalid.", fields);
        }

        private void Add(string field, string message)
        {
            if (!_fields.TryGetValue(field, out var list))
            {
                _fields[field] = list = [];
            }

            list.Add(message);
        }
    }
}
