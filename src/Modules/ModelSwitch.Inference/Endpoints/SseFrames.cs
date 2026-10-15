using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Inference.Endpoints;

/// <summary>
/// The Server-Sent Events frames of a streamed chat completion (docs/api.md section 1), written as UTF-8 straight into a
/// buffer with one reused <see cref="Utf8JsonWriter"/>: no per-chunk DOM, string building or LINQ (architecture.md section 7).
/// Each method appends one complete event to the output and does not flush it.
/// </summary>
internal sealed class SseFrames : IDisposable
{
    private static readonly JsonWriterOptions _options = new()
    {
        // The text is JSON-escaped either way; relaxed escaping just keeps non-ASCII characters as UTF-8 instead of \uXXXX.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = false,
    };

    private readonly IBufferWriter<byte> _output;
    private readonly Utf8JsonWriter _json;
    private readonly string _id;
    private readonly long _created;
    private readonly string _model;
    private readonly string _requestId;

    public SseFrames(IBufferWriter<byte> output, Guid requestId, string alias, long created)
    {
        _output = output;
        _json = new Utf8JsonWriter(output, _options);
        _requestId = requestId.ToString();
        _id = $"chatcmpl-{_requestId}";
        _created = created;
        _model = alias;
    }

    public void Dispose() => _json.Dispose();

    /// <summary><c>data: {…"delta":{"role":"assistant"}…}</c>: sent with the First Token, never before it.</summary>
    public void WriteRole()
    {
        Begin();
        WriteChunkStart();
        _json.WriteStartArray("choices");
        _json.WriteStartObject();
        _json.WriteNumber("index", 0);
        _json.WriteStartObject("delta");
        _json.WriteString("role", "assistant");
        _json.WriteEndObject();
        _json.WriteNull("finish_reason");
        _json.WriteEndObject();
        _json.WriteEndArray();
        _json.WriteEndObject();
        End();
    }

    public void WriteContent(string text)
    {
        Begin();
        WriteChunkStart();
        _json.WriteStartArray("choices");
        _json.WriteStartObject();
        _json.WriteNumber("index", 0);
        _json.WriteStartObject("delta");
        _json.WriteString("content", text);
        _json.WriteEndObject();
        _json.WriteNull("finish_reason");
        _json.WriteEndObject();
        _json.WriteEndArray();
        _json.WriteEndObject();
        End();
    }

    public void WriteFinish(FinishReason reason)
    {
        Begin();
        WriteChunkStart();
        _json.WriteStartArray("choices");
        _json.WriteStartObject();
        _json.WriteNumber("index", 0);
        _json.WriteStartObject("delta");
        _json.WriteEndObject();
        _json.WriteString("finish_reason", ChatCompletionWire.FinishReasonText(reason));
        _json.WriteEndObject();
        _json.WriteEndArray();
        _json.WriteEndObject();
        End();
    }

    /// <summary>The last chunk: empty <c>choices</c>, the usage and the gateway's metadata (<c>stream_options.include_usage</c>).</summary>
    public void WriteUsage(NeutralUsage usage, string usageSource, int attempts)
    {
        Begin();
        WriteChunkStart();
        _json.WriteStartArray("choices");
        _json.WriteEndArray();
        _json.WriteStartObject("usage");
        _json.WriteNumber("prompt_tokens", usage.InputTokens);
        _json.WriteNumber("completion_tokens", usage.OutputTokens);
        _json.WriteNumber("total_tokens", (long)usage.InputTokens + usage.OutputTokens);
        _json.WriteEndObject();
        _json.WriteStartObject("modelswitch");
        _json.WriteString("request_id", _requestId);
        _json.WriteString("usage_source", usageSource);
        _json.WriteNumber("attempts", attempts);
        _json.WriteEndObject();
        _json.WriteEndObject();
        End();
    }

    /// <summary>The mid-stream error event (FR-05): the OpenAI <c>error</c> object, after which the stream ends without <c>[DONE]</c>.</summary>
    public void WriteError(Error error)
    {
        Begin();
        _json.WriteStartObject();
        _json.WriteStartObject("error");
        _json.WriteString("message", error.Message);
        _json.WriteString("type", error.Code.Type);
        _json.WriteString("code", error.Code.Value);
        _json.WriteNull("param");
        _json.WriteString("request_id", _requestId);
        _json.WriteEndObject();
        _json.WriteEndObject();
        End();
    }

    public void WriteDone() => Raw("data: [DONE]\n\n"u8);

    /// <summary>An SSE comment: ignored by every client, but it keeps proxies and idle timeouts from closing a quiet connection.</summary>
    public void WriteKeepAlive() => Raw(": keep-alive\n\n"u8);

    private void WriteChunkStart()
    {
        _json.WriteStartObject();
        _json.WriteString("id", _id);
        _json.WriteString("object", "chat.completion.chunk");
        _json.WriteNumber("created", _created);
        _json.WriteString("model", _model);
    }

    private void Begin()
    {
        Raw("data: "u8);
        _json.Reset();
    }

    private void End()
    {
        _json.Flush();
        Raw("\n\n"u8);
    }

    private void Raw(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(_output.GetSpan(bytes.Length));
        _output.Advance(bytes.Length);
    }
}
