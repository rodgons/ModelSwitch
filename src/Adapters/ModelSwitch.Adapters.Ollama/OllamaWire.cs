using System.Buffers;
using System.Text.Json;
using ModelSwitch.Adapters.Abstractions;

namespace ModelSwitch.Adapters.Ollama;

/// <summary>What one Ollama response object (a whole non-streaming body, or one NDJSON line) says. Only the fields the Adapter uses.</summary>
internal struct OllamaFrame
{
    public string? Content;
    public bool HasMessage;
    public bool Done;
    public string? DoneReason;
    public int? PromptEvalCount;
    public int? EvalCount;
    public int? CachedCount;
    public bool HasError;
    public string? ErrorCode;
}

/// <summary>Reads and writes Ollama's JSON. Parsing uses <see cref="Utf8JsonReader"/> directly so a streamed chunk allocates nothing but its text.</summary>
internal static class OllamaWire
{
    /// <summary>Parses one JSON object. Returns false when the bytes are not a well-formed JSON object.</summary>
    public static bool TryParseFrame(in ReadOnlySequence<byte> json, out OllamaFrame frame)
    {
        frame = default;
        try
        {
            var reader = new Utf8JsonReader(json, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("message"u8))
                {
                    reader.Read();
                    ReadMessage(ref reader, ref frame);
                }
                else if (reader.ValueTextEquals("done"u8))
                {
                    reader.Read();
                    frame.Done = reader.TokenType == JsonTokenType.True;
                }
                else if (reader.ValueTextEquals("done_reason"u8))
                {
                    reader.Read();
                    frame.DoneReason = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                }
                else if (reader.ValueTextEquals("prompt_eval_count"u8))
                {
                    frame.PromptEvalCount = ReadInt(ref reader);
                }
                else if (reader.ValueTextEquals("eval_count"u8))
                {
                    frame.EvalCount = ReadInt(ref reader);
                }
                else if (reader.ValueTextEquals("prompt_eval_cached_count"u8))
                {
                    frame.CachedCount = ReadInt(ref reader);
                }
                else if (reader.ValueTextEquals("error"u8))
                {
                    reader.Read();
                    frame.HasError = true;
                    ReadError(ref reader, ref frame);
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            return reader.TokenType == JsonTokenType.EndObject;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            frame = default;
            return false;
        }
    }

    /// <summary>The Provider-reported usage, or null when Ollama did not report both counts (the Adapter never estimates).</summary>
    public static NeutralUsage? ToUsage(in OllamaFrame frame, out bool invalidCached)
    {
        invalidCached = false;
        if (frame.PromptEvalCount is not { } input || frame.EvalCount is not { } output)
        {
            return null;
        }

        var cached = frame.CachedCount;
        if (cached > input)
        {
            invalidCached = true;
            cached = null;
        }

        return new NeutralUsage(input, output, cached);
    }

    public static FinishReason ToFinishReason(string? doneReason) => doneReason switch
    {
        "length" => FinishReason.Length,
        "content_filter" => FinishReason.ContentFilter,
        _ => FinishReason.Stop,
    };

    public static byte[] WriteChatRequest(AdapterContext ctx, NeutralChatRequest request, bool stream)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", ctx.ProviderModelId);
            writer.WriteStartArray("messages");
            foreach (var message in request.Messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", message.Role switch
                {
                    MessageRole.System => "system",
                    MessageRole.Assistant => "assistant",
                    _ => "user",
                });
                writer.WriteString("content", message.Text);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteBoolean("stream", stream);
            switch (request.ResponseFormat)
            {
                case ResponseFormat.JsonObject:
                    writer.WriteString("format", "json");
                    break;
                case ResponseFormat.JsonSchema schema:
                    writer.WritePropertyName("format");
                    schema.Schema.WriteTo(writer);
                    break;
            }

            WriteOptions(writer, request);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteOptions(Utf8JsonWriter writer, NeutralChatRequest request)
    {
        var hasStop = request.Stop is { Count: > 0 };
        if (request.MaxOutputTokens is null && request.Temperature is null && request.TopP is null && request.PresencePenalty is null
            && request.FrequencyPenalty is null && request.Seed is null && !hasStop)
        {
            return;
        }

        writer.WriteStartObject("options");
        if (request.MaxOutputTokens is { } max)
        {
            writer.WriteNumber("num_predict", max);
        }

        if (request.Temperature is { } temperature)
        {
            writer.WriteNumber("temperature", temperature);
        }

        if (request.TopP is { } topP)
        {
            writer.WriteNumber("top_p", topP);
        }

        if (request.PresencePenalty is { } presence)
        {
            writer.WriteNumber("presence_penalty", presence);
        }

        if (request.FrequencyPenalty is { } frequency)
        {
            writer.WriteNumber("frequency_penalty", frequency);
        }

        if (request.Seed is { } seed)
        {
            writer.WriteNumber("seed", seed);
        }

        if (hasStop)
        {
            writer.WriteStartArray("stop");
            foreach (var stop in request.Stop!)
            {
                writer.WriteStringValue(stop);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void ReadMessage(ref Utf8JsonReader reader, ref OllamaFrame frame)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return;
        }

        frame.HasMessage = true;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("content"u8))
            {
                reader.Read();
                frame.Content = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }
    }

    /// <summary>Ollama's own errors are a bare string. An object with a <c>code</c> is what reverse proxies in front of it emit.</summary>
    private static void ReadError(ref Utf8JsonReader reader, ref OllamaFrame frame)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("code"u8))
            {
                reader.Read();
                frame.ErrorCode = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }
    }

    private static int? ReadInt(ref Utf8JsonReader reader)
    {
        reader.Read();
        return reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value) ? value : null;
    }
}
