using System.Text;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Inference.Contracts;

/// <summary>
/// One piece of a streamed completion for the client. Exactly one <see cref="Completed"/> or <see cref="Interrupted"/> ends the
/// sequence; <see cref="Content"/> pieces come before it.
/// </summary>
public abstract record StreamPart
{
    private StreamPart()
    {
    }

    /// <summary>A piece of completion text: content (ADR-0010), excluded from <see cref="object.ToString"/>.</summary>
    public sealed record Content(string Text) : StreamPart
    {
        protected override bool PrintMembers(StringBuilder builder)
        {
            builder.Append("Text = <redacted>");
            return true;
        }
    }

    /// <summary>The Provider finished. <see cref="Usage"/> is what it reported, or the gateway's estimate when it reported none.</summary>
    /// <param name="Reason">Why the Provider stopped.</param>
    /// <param name="Usage">Token counts for the final usage chunk.</param>
    /// <param name="UsageSource"><c>reported</c> or <c>estimated</c>.</param>
    /// <param name="Attempts">The number of Attempts made, including the ones before the First Token.</param>
    public sealed record Completed(FinishReason Reason, NeutralUsage Usage, string UsageSource, int Attempts) : StreamPart;

    /// <summary>
    /// The Provider failed after the First Token had gone out, so there is no retry and no Fallback and the client has partial
    /// output (FR-05). The error is always <c>stream_interrupted</c>.
    /// </summary>
    public sealed record Interrupted(Error Error) : StreamPart;
}

/// <summary>
/// A streaming completion whose First Token has been received from a Provider: nothing has been sent to the client yet, so
/// everything up to here could still fail as an ordinary error. Read it once with <see cref="ReadAsync"/>; dispose it
/// whatever happens (reading to the end, an early stop or never reading) so the Provider connection is closed and the
/// request is settled.
/// </summary>
public sealed class StreamingCompletion : IAsyncDisposable
{
    private readonly Func<CancellationToken, IAsyncEnumerable<StreamPart>> _read;
    private readonly Func<ValueTask> _abandon;
    private int _state;

    internal StreamingCompletion(Guid requestId, string alias, Func<CancellationToken, IAsyncEnumerable<StreamPart>> read, Func<ValueTask> abandon)
    {
        RequestId = requestId;
        Alias = alias;
        _read = read;
        _abandon = abandon;
    }

    /// <summary>The Inference Request ID, also in <c>X-Request-Id</c>.</summary>
    public Guid RequestId { get; }

    /// <summary>The Model Alias, echoed instead of the Provider Model ID (FR-02).</summary>
    public string Alias { get; }

    /// <summary>
    /// The parts, ending with <see cref="StreamPart.Completed"/> or <see cref="StreamPart.Interrupted"/>. Cancelling
    /// <paramref name="cancellationToken"/> (the client disconnected) aborts the Provider stream and throws
    /// <see cref="OperationCanceledException"/>; the request is still settled, on a fresh token. Settlement of a finished stream
    /// happens when the enumeration ends, after the last part has been handed over.
    /// </summary>
    public IAsyncEnumerable<StreamPart> ReadAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            throw new InvalidOperationException("An inference stream can be read once.");
        }

        return _read(cancellationToken);
    }

    public ValueTask DisposeAsync()
        => Interlocked.CompareExchange(ref _state, 2, 0) == 0 ? _abandon() : ValueTask.CompletedTask;
}
