using System.Text;
using System.Text.Json;

namespace ModelSwitch.Adapters.Abstractions;

public enum MessageRole
{
    System,
    User,
    Assistant,
}

/// <summary>One chat message. The text is prompt content (ADR-0010): it is excluded from <see cref="object.ToString"/>.</summary>
public sealed record NeutralMessage(MessageRole Role, string Text)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Role = ").Append(Role).Append(", Text = <redacted>");
        return true;
    }
}

/// <summary>How the Provider is asked to shape its output. Maps 1:1 to the OpenAI <c>response_format</c>.</summary>
public abstract record ResponseFormat
{
    private ResponseFormat()
    {
    }

    public sealed record Text : ResponseFormat;

    public sealed record JsonObject : ResponseFormat;

    public sealed record JsonSchema(string Name, JsonElement Schema, bool Strict) : ResponseFormat;
}

/// <summary>The gateway's provider-neutral chat request. Every sampling parameter is nullable: null means "do not send".</summary>
public sealed record NeutralChatRequest(IReadOnlyList<NeutralMessage> Messages)
{
    public int? MaxOutputTokens { get; init; }

    public double? Temperature { get; init; }

    public double? TopP { get; init; }

    public double? PresencePenalty { get; init; }

    public double? FrequencyPenalty { get; init; }

    public IReadOnlyList<string>? Stop { get; init; }

    public long? Seed { get; init; }

    public ResponseFormat ResponseFormat { get; init; } = new ResponseFormat.Text();
}
