using System.Text;

namespace ModelSwitch.Adapters.Abstractions;

/// <summary>Why the Provider stopped generating. Content filtering is a finish reason, not a failure.</summary>
public enum FinishReason
{
    Stop,
    Length,
    ContentFilter,
}

/// <summary>
/// Token counts exactly as the Provider reported them. An Adapter never estimates.
/// <see cref="CachedInputTokens"/> is a subset of <see cref="InputTokens"/>: null when not reported, 0 when the Provider reported none.
/// </summary>
public sealed record NeutralUsage(int InputTokens, int OutputTokens, int? CachedInputTokens);

/// <summary>The result of a non-streaming call: either <see cref="Success"/> or <see cref="Failure"/>.</summary>
public abstract record CompletionOutcome
{
    private CompletionOutcome()
    {
    }

    /// <summary>The completion text is content (ADR-0010) and is excluded from <see cref="object.ToString"/>.</summary>
    public sealed record Success(string Text, FinishReason FinishReason, NeutralUsage? Usage) : CompletionOutcome
    {
        protected override bool PrintMembers(StringBuilder builder)
        {
            builder.Append("Text = <redacted>, FinishReason = ").Append(FinishReason).Append(", Usage = ").Append(Usage);
            return true;
        }
    }

    public sealed record Failure(ProviderFailure Error) : CompletionOutcome;
}
