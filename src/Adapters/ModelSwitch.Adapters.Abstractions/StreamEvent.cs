using System.Text;

namespace ModelSwitch.Adapters.Abstractions;

/// <summary>
/// One item of a streamed completion. The stream always ends with exactly one <see cref="Finish"/> or <see cref="Failed"/>;
/// <see cref="Usage"/> may come before or after <see cref="Finish"/>.
/// </summary>
public abstract record StreamEvent
{
    private StreamEvent()
    {
    }

    /// <summary>A piece of completion text (content, ADR-0010: excluded from <see cref="object.ToString"/>).</summary>
    public sealed record ContentDelta(string Text) : StreamEvent
    {
        protected override bool PrintMembers(StringBuilder builder)
        {
            builder.Append("Text = <redacted>");
            return true;
        }
    }

    public sealed record Finish(FinishReason Reason) : StreamEvent;

    public sealed record Usage(NeutralUsage Value) : StreamEvent;

    public sealed record Failed(ProviderFailure Error) : StreamEvent;
}
