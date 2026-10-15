using System.Text.Json;
using ModelSwitch.Adapters.Abstractions;

namespace ModelSwitch.Adapters.OpenAICompatible.Wire;

/// <summary>
/// Extracts what the Adapter needs from one Chat Completions JSON document: a non-streaming response body, a stream chunk, or an
/// error body. It reads with <see cref="Utf8JsonReader"/> and skips everything else, so unknown fields (OpenRouter's <c>provider</c>,
/// OpenAI's <c>obfuscation</c>, reasoning details...) cost nothing. One instance is reused per stream; <see cref="Parse"/> resets it.
/// Content and error text are never kept: only the error's <c>code</c> and <c>type</c>.
/// </summary>
internal sealed class ChatPayloadParser
{
    /// <summary>The first choice's <c>message.content</c> or <c>delta.content</c>.</summary>
    public string? Content { get; private set; }

    /// <summary>The first choice's <c>finish_reason</c>, when non-null.</summary>
    public string? FinishReason { get; private set; }

    /// <summary>Whether the document had a non-empty <c>choices</c> array.</summary>
    public bool HasChoice { get; private set; }

    public NeutralUsage? Usage { get; private set; }

    /// <summary>The Provider reported more cached than input tokens (the Adapter dropped the cached figure).</summary>
    public bool UsageInconsistent { get; private set; }

    /// <summary>The document carries an <c>error</c> member.</summary>
    public bool HasError { get; private set; }

    /// <summary>The error's <c>code</c> when it is a string (OpenAI style).</summary>
    public string? ErrorCode { get; private set; }

    /// <summary>The error's <c>code</c> when it is a number (OpenRouter style: an HTTP status).</summary>
    public int? ErrorStatus { get; private set; }

    public string? ErrorType { get; private set; }

    /// <summary>
    /// The error's message says the prompt exceeded the model's context window. OpenAI also sends <c>code = context_length_exceeded</c>;
    /// OpenRouter sends only a numeric 400 and the sentence. The message text itself is never kept.
    /// </summary>
    public bool ErrorMentionsContextLength { get; private set; }

    /// <summary>Parses one JSON document. Returns false when it is not a JSON object.</summary>
    public bool Parse(ReadOnlySpan<byte> json)
    {
        Reset();
        try
        {
            var reader = new Utf8JsonReader(json, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("choices"u8))
                {
                    reader.Read();
                    ReadChoices(ref reader);
                }
                else if (reader.ValueTextEquals("usage"u8))
                {
                    reader.Read();
                    ReadUsage(ref reader);
                }
                else if (reader.ValueTextEquals("error"u8))
                {
                    reader.Read();
                    ReadError(ref reader);
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            return reader.TokenType == JsonTokenType.EndObject;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void Reset()
    {
        Content = null;
        FinishReason = null;
        HasChoice = false;
        Usage = null;
        UsageInconsistent = false;
        HasError = false;
        ErrorCode = null;
        ErrorStatus = null;
        ErrorType = null;
        ErrorMentionsContextLength = false;
    }

    private void ReadChoices(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return;
        }

        var first = true;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (!first || reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            first = false;
            HasChoice = true;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("message"u8) || reader.ValueTextEquals("delta"u8))
                {
                    reader.Read();
                    ReadMessage(ref reader);
                }
                else if (reader.ValueTextEquals("finish_reason"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        FinishReason = reader.GetString();
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
        }
    }

    private void ReadMessage(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("content"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String)
                {
                    Content = reader.GetString();
                }
                else
                {
                    reader.Skip();
                }
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }
    }

    private void ReadUsage(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return;
        }

        int? input = null;
        int? output = null;
        int? cachedDetail = null;
        int? cachedFlat = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("prompt_tokens"u8))
            {
                input = ReadInt(ref reader);
            }
            else if (reader.ValueTextEquals("completion_tokens"u8))
            {
                output = ReadInt(ref reader);
            }
            else if (reader.ValueTextEquals("cached_tokens"u8))
            {
                cachedFlat = ReadInt(ref reader);
            }
            else if (reader.ValueTextEquals("prompt_tokens_details"u8))
            {
                reader.Read();
                cachedDetail = ReadCachedDetail(ref reader);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (input is not { } inputTokens)
        {
            return;
        }

        var cached = cachedDetail ?? cachedFlat;
        if (cached > inputTokens)
        {
            cached = null;
            UsageInconsistent = true;
        }

        Usage = new NeutralUsage(inputTokens, output ?? 0, cached);
    }

    private static int? ReadCachedDetail(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        int? cached = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("cached_tokens"u8))
            {
                cached = ReadInt(ref reader);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        return cached;
    }

    /// <summary>Advances to the property value and returns it when it is an <see cref="int"/>, else null.</summary>
    private static int? ReadInt(ref Utf8JsonReader reader)
    {
        reader.Read();
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value))
        {
            return value;
        }

        reader.Skip();
        return null;
    }

    private void ReadError(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return;
            case JsonTokenType.StartObject:
                HasError = true;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("code"u8))
                    {
                        reader.Read();
                        if (reader.TokenType == JsonTokenType.String)
                        {
                            ErrorCode = reader.GetString();
                        }
                        else if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var status))
                        {
                            ErrorStatus = status;
                        }
                        else
                        {
                            reader.Skip();
                        }
                    }
                    else if (reader.ValueTextEquals("message"u8))
                    {
                        reader.Read();
                        if (reader.TokenType == JsonTokenType.String)
                        {
                            ErrorMentionsContextLength = reader.GetString()?.Contains("maximum context length", StringComparison.OrdinalIgnoreCase) == true;
                        }
                        else
                        {
                            reader.Skip();
                        }
                    }
                    else if (reader.ValueTextEquals("type"u8))
                    {
                        reader.Read();
                        if (reader.TokenType == JsonTokenType.String)
                        {
                            ErrorType = reader.GetString();
                        }
                        else
                        {
                            reader.Skip();
                        }
                    }
                    else
                    {
                        reader.Read();
                        reader.Skip();
                    }
                }

                break;
            default:
                // A bare string or other shape: an error with nothing classifiable inside.
                HasError = true;
                reader.Skip();
                break;
        }
    }
}
