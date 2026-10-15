using System.IO.Pipelines;
using ModelSwitch.Inference.Contracts;

namespace ModelSwitch.Inference.Endpoints;

/// <summary>
/// Copies a <see cref="StreamingCompletion"/> to the client as Server-Sent Events: each part is written and flushed as soon as
/// it arrives, a keep-alive comment goes out whenever the stream has been quiet for the interval, and the ending is the
/// documented one: <c>[DONE]</c> after a completion, the error event and no <c>[DONE]</c> after an interruption (api.md section 1).
/// Transport only: it decides nothing about the completion.
/// </summary>
internal sealed class SsePump(TimeProvider time, TimeSpan keepAliveInterval)
{
    /// <summary>
    /// Returns when the stream is done or the client can no longer be written to. Throws <see cref="OperationCanceledException"/>
    /// when <paramref name="cancellationToken"/> (the client disconnected) is cancelled; the enumeration is then disposed, which
    /// aborts the Provider stream.
    /// </summary>
    public async Task PumpAsync(PipeWriter output, StreamingCompletion stream, bool includeUsage, long created, CancellationToken cancellationToken)
    {
        using var frames = new SseFrames(output, stream.RequestId, stream.Alias, created);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var enumerator = stream.ReadAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            var lastWrite = time.GetTimestamp();
            Task? tick = null;
            var roleSent = false;
            while (true)
            {
                var moving = enumerator.MoveNextAsync();
                bool moved;
                if (moving.IsCompleted)
                {
                    moved = await moving;
                }
                else
                {
                    var pending = moving.AsTask();
                    while (!pending.IsCompleted)
                    {
                        var wait = keepAliveInterval - time.GetElapsedTime(lastWrite);
                        if (wait <= TimeSpan.Zero)
                        {
                            frames.WriteKeepAlive();
                            if (!await FlushAsync(output, cancellationToken))
                            {
                                return;
                            }

                            lastWrite = time.GetTimestamp();
                            continue;
                        }

                        // One timer serves many chunks: a stale one that fires early just finds time left and is replaced.
                        tick ??= Task.Delay(wait, time, idle.Token);
                        await Task.WhenAny(pending, tick);
                        if (tick.IsCompleted)
                        {
                            tick = null;
                        }
                    }

                    moved = await pending;
                }

                if (!moved)
                {
                    return;
                }

                switch (enumerator.Current)
                {
                    case StreamPart.Content content:
                        if (!roleSent)
                        {
                            frames.WriteRole();
                            roleSent = true;
                        }

                        frames.WriteContent(content.Text);
                        break;

                    case StreamPart.Completed completed:
                        if (!roleSent)
                        {
                            frames.WriteRole();
                        }

                        frames.WriteFinish(completed.Reason);
                        if (includeUsage)
                        {
                            frames.WriteUsage(completed.Usage, completed.UsageSource, completed.Attempts);
                        }

                        frames.WriteDone();
                        break;

                    case StreamPart.Interrupted interrupted:
                        frames.WriteError(interrupted.Error);
                        break;
                }

                if (!await FlushAsync(output, cancellationToken))
                {
                    return;
                }

                lastWrite = time.GetTimestamp();
            }
        }
        finally
        {
            await idle.CancelAsync();
            await enumerator.DisposeAsync();
        }
    }

    // False once the client has gone away: there is nobody left to write to.
    private static async Task<bool> FlushAsync(PipeWriter output, CancellationToken cancellationToken)
    {
        var flushed = await output.FlushAsync(cancellationToken);
        return !flushed.IsCompleted && !flushed.IsCanceled;
    }
}
