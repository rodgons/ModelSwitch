using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using ModelSwitch.Adapters.Abstractions;

namespace ModelSwitch.Adapters.OpenAICompatible.Wire;

/// <summary>Serializes a <see cref="NeutralChatRequest"/> as an OpenAI Chat Completions request body.</summary>
internal static class ChatRequestWriter
{
    public static ByteArrayContent Write(AdapterContext ctx, NeutralChatRequest request, bool stream, OpenAICompatibleOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("model", ctx.ProviderModelId);

            json.WriteStartArray("messages");
            foreach (var message in request.Messages)
            {
                json.WriteStartObject();
                json.WriteString("role", RoleName(message.Role));
                json.WriteString("content", message.Text);
                json.WriteEndObject();
            }

            json.WriteEndArray();

            json.WriteBoolean("stream", stream);
            if (stream)
            {
                json.WriteStartObject("stream_options");
                json.WriteBoolean("include_usage", true);
                json.WriteEndObject();
            }

            if (request.MaxOutputTokens is { } max)
            {
                json.WriteNumber(options.UseLegacyMaxTokens ? "max_tokens" : "max_completion_tokens", max);
            }

            WriteIfSet(json, "temperature", request.Temperature);
            WriteIfSet(json, "top_p", request.TopP);
            WriteIfSet(json, "presence_penalty", request.PresencePenalty);
            WriteIfSet(json, "frequency_penalty", request.FrequencyPenalty);
            if (request.Seed is { } seed)
            {
                json.WriteNumber("seed", seed);
            }

            if (request.Stop is { Count: > 0 } stop)
            {
                json.WriteStartArray("stop");
                foreach (var sequence in stop)
                {
                    json.WriteStringValue(sequence);
                }

                json.WriteEndArray();
            }

            WriteResponseFormat(json, request.ResponseFormat);

            if (options.PinnedUpstreams is { Count: > 0 } upstreams)
            {
                json.WriteStartObject("provider");
                json.WriteStartArray("only");
                foreach (var upstream in upstreams)
                {
                    json.WriteStringValue(upstream);
                }

                json.WriteEndArray();
                json.WriteBoolean("allow_fallbacks", false);
                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        var content = new ByteArrayContent(buffer.WrittenSpan.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private static void WriteResponseFormat(Utf8JsonWriter json, ResponseFormat format)
    {
        switch (format)
        {
            case ResponseFormat.JsonObject:
                json.WriteStartObject("response_format");
                json.WriteString("type", "json_object");
                json.WriteEndObject();
                break;
            case ResponseFormat.JsonSchema schema:
                json.WriteStartObject("response_format");
                json.WriteString("type", "json_schema");
                json.WriteStartObject("json_schema");
                json.WriteString("name", schema.Name);
                json.WriteBoolean("strict", schema.Strict);
                json.WritePropertyName("schema");
                schema.Schema.WriteTo(json);
                json.WriteEndObject();
                json.WriteEndObject();
                break;
        }
    }

    private static void WriteIfSet(Utf8JsonWriter json, string name, double? value)
    {
        if (value is { } v)
        {
            json.WriteNumber(name, v);
        }
    }

    private static string RoleName(MessageRole role) => role switch
    {
        MessageRole.System => "system",
        MessageRole.Assistant => "assistant",
        _ => "user",
    };
}
