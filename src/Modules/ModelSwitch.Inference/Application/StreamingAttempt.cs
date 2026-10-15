using System.Runtime.CompilerServices;
using System.Text;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Inference.Application;

/// <summary>How opening a Provider stream ended: with the First Token in hand, or with a classified failure.</summary>
internal abstract record StreamStart
{
    private StreamStart()
    {
    }

    internal sealed record Started(StreamingAttempt Attempt) : StreamStart;

    internal sealed record Failed(ProviderFailure Error) : StreamStart;
}

/// <summary>
/// One streaming Attempt whose First Token has arrived (routing-and-resilience.md section 6). It owns the open Provider stream
/// and what was read ahead of the First Token, forwards the rest as <see cref="StreamPart"/>s, and appends the Attempt's record
/// to the request's list exactly once when the stream ends, however it ends. Past this point nothing is retried: a Provider
/// failure becomes <see cref="StreamPart.Interrupted"/>. Content is only ever passed through; it is kept in memory solely to
/// estimate the output tokens of a stream that ended without usage, and never logged or persisted (ADR-0010).
/// </summary>
internal sealed class StreamingAttempt : IAsyncDisposable
{
    private readonly AttemptExecutor _executor;
    private readonly AttemptExecutor.CallInput _input;
    private readonly TimeProvider _time;
    private readonly CancellationToken _client;
    private readonly IAsyncEnumerator<StreamEvent> _events;
    private readonly CancellationTokenSource _stall;
    private readonly CancellationTokenSource _linked;
    private readonly List<StreamEvent> _ahead = new(4);
    private readonly StringBuilder _forwarded = new();
    private List<AttemptRecord>? _attempts;
    private AttemptExecutor.AttemptContext _attempt;
    private int? _ttftMs;
    private bool _recorded;
    private bool _disposed;

    private StreamingAttempt(
        AttemptExecutor executor,
        AttemptExecutor.CallInput input,
        TimeProvider time,
        IAsyncEnumerator<StreamEvent> events,
        CancellationTokenSource stall,
        CancellationTokenSource linked,
        CancellationToken client)
    {
        _executor = executor;
        _input = input;
        _time = time;
        _client = client;
        _events = events;
        _stall = stall;
        _linked = linked;
    }

    /// <summary>
    /// Opens the Provider stream and reads until the First Token: the first non-empty content chunk, or the end of an empty
    /// completion. Role-only and empty deltas are skipped and <see cref="StreamEvent.Usage"/> read ahead is kept, so the retry
    /// window stays open as long as possible. The whole wait is bounded by the Attempt timeout. Throws
    /// <see cref="OperationCanceledException"/> when <paramref name="client"/> is cancelled.
    /// </summary>
    internal static async Task<StreamStart> OpenAsync(AttemptExecutor executor, AttemptExecutor.CallInput input, TimeProvider time, CancellationToken client)
    {
        var stall = new CancellationTokenSource(Timeout.InfiniteTimeSpan, time);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(client, stall.Token);
        StreamingAttempt? attempt = null;
        var handedOver = false;
        try
        {
            var events = input.Adapter.StreamAsync(input.Context, input.Chat, linked.Token).GetAsyncEnumerator(linked.Token);
            attempt = new StreamingAttempt(executor, input, time, events, stall, linked, client);
            var failure = await attempt.ReadUntilFirstTokenAsync();
            if (failure is not null)
            {
                return new StreamStart.Failed(failure);
            }

            handedOver = true;
            return new StreamStart.Started(attempt);
        }
        finally
        {
            if (!handedOver)
            {
                if (attempt is not null)
                {
                    await attempt.DisposeAsync();
                }
                else
                {
                    linked.Dispose();
                    stall.Dispose();
                }
            }
        }
    }

    /// <summary>Called by the loop once the circuit has let this Attempt through: from here the Attempt records itself.</summary>
    internal StreamingAttempt Begin(List<AttemptRecord> attempts, AttemptExecutor.AttemptContext attempt)
    {
        _attempts = attempts;
        _attempt = attempt;
        _ttftMs = (int)Math.Clamp((_time.GetUtcNow() - attempt.StartedAt).TotalMilliseconds, 0, int.MaxValue);
        return this;
    }

    /// <summary>
    /// Forwards the stream: first what was read ahead, then the rest, ending with one terminal part. The Attempt is recorded
    /// before the terminal part is handed over, so a settlement that follows sees it. If the consumer stops early or
    /// <paramref name="cancellationToken"/> (the client) is cancelled, the Attempt is recorded ambiguous: the Provider was
    /// mid-generation and may bill for it.
    /// </summary>
    internal async IAsyncEnumerable<StreamPart> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        FinishReason? finish = null;
        NeutralUsage? usage = null;
        ProviderFailure? failure = null;
        var next = 0;
        try
        {
            while (true)
            {
                StreamEvent? current;
                if (next < _ahead.Count)
                {
                    current = _ahead[next++];
                }
                else
                {
                    try
                    {
                        current = await NextAsync(_input.StallTimeout);
                    }
                    catch (OperationCanceledException) when (_client.IsCancellationRequested || cancellationToken.IsCancellationRequested)
                    {
                        RecordCancelled();
                        throw;
                    }
                }

                if (current is null)
                {
                    break;
                }

                if (current is StreamEvent.ContentDelta delta)
                {
                    _forwarded.Append(delta.Text);
                    yield return new StreamPart.Content(delta.Text);
                }
                else if (current is StreamEvent.Finish done)
                {
                    finish = done.Reason;
                }
                else if (current is StreamEvent.Usage reported)
                {
                    usage = reported.Value;
                }
                else if (current is StreamEvent.Failed failed)
                {
                    failure = failed.Error;
                    break;
                }
            }

            if (finish is { } reason)
            {
                // The Provider said it was done; a hiccup after that costs the client nothing.
                var ended = _executor.EndStream(_input.Chat, _attempt, AttemptStatus.Succeeded, null, null, null, null, usage, _forwarded.ToString(), _ttftMs);
                Record(ended.Record);
                yield return new StreamPart.Completed(reason, ended.Usage, ended.Source == UsageSource.Reported ? "reported" : "estimated", CountCalled());
                yield break;
            }

            failure ??= new ProviderFailure(FailureClass.Transient, ProviderFailureCodes.InvalidProviderResponse, 200, null, RequestSent: true);
            var errorClass = failure.Class switch
            {
                FailureClass.Ambiguous => AttemptErrorClass.Ambiguous,
                FailureClass.Permanent => AttemptErrorClass.Permanent,
                _ => AttemptErrorClass.Transient,
            };
            var interrupted = _executor.EndStream(
                _input.Chat,
                _attempt,
                failure.Class == FailureClass.Ambiguous ? AttemptStatus.Ambiguous : AttemptStatus.Failed,
                errorClass,
                ErrorCode.StreamInterrupted.Value,
                failure.HttpStatus,
                failure.Code,
                usage,
                _forwarded.ToString(),
                _ttftMs);
            Record(interrupted.Record);
            await _executor.ReportStreamFailureAsync(_attempt.Target.ProviderModelId, failure);
            yield return new StreamPart.Interrupted(new Error(ErrorCode.StreamInterrupted, "The stream was interrupted before it completed."));
        }
        finally
        {
            // Reached without a terminal part when the consumer stopped early (it could no longer write to the client).
            RecordCancelled();
            await DisposeAsync();
        }
    }

    /// <summary>The consumer never read the stream (or gave up before the first part): close the Provider and record the Attempt.</summary>
    internal async ValueTask AbandonAsync()
    {
        RecordCancelled();
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Disposing the enumerator aborts the HTTP response: the Provider stops generating.
        await _events.DisposeAsync();
        _linked.Dispose();
        _stall.Dispose();
    }

    private async Task<ProviderFailure?> ReadUntilFirstTokenAsync()
    {
        var started = _time.GetTimestamp();
        while (true)
        {
            var remaining = _input.AttemptTimeout - _time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return TimeoutFailure();
            }

            var current = await NextAsync(remaining);
            switch (current)
            {
                case null:
                    // An Adapter always ends a stream with Finish or Failed; one that does not is broken, not the Provider's fault to retry blindly.
                    return new ProviderFailure(FailureClass.Transient, ProviderFailureCodes.InvalidProviderResponse, 200, null, RequestSent: true);
                case StreamEvent.Failed failed:
                    return failed.Error;
                case StreamEvent.ContentDelta { Text.Length: > 0 }:
                case StreamEvent.Finish:
                    _ahead.Add(current);
                    return null;
                case StreamEvent.Usage:
                    _ahead.Add(current);
                    break;
            }
        }
    }

    // Waits for the next event, but not longer than the limit: a Provider that goes quiet is as bad as one that fails.
    // Returns null when the stream ended, and a Failed (timeout) when it stalled. Client cancellation propagates.
    private async ValueTask<StreamEvent?> NextAsync(TimeSpan limit)
    {
        _stall.CancelAfter(limit);
        try
        {
            return await _events.MoveNextAsync() ? _events.Current : null;
        }
        catch (OperationCanceledException) when (!_client.IsCancellationRequested && _stall.IsCancellationRequested)
        {
            return new StreamEvent.Failed(TimeoutFailure());
        }
        finally
        {
            _stall.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    private static ProviderFailure TimeoutFailure()
        => new(FailureClass.Ambiguous, ProviderFailureCodes.Timeout, null, null, RequestSent: true);

    private void RecordCancelled()
    {
        if (_recorded)
        {
            return;
        }

        var ended = _executor.EndStream(
            _input.Chat,
            _attempt,
            AttemptStatus.Ambiguous,
            AttemptErrorClass.Ambiguous,
            errorCode: null,
            httpStatus: null,
            AttemptExecutor.ProviderErrorTypes.ClientCancelled,
            reported: null,
            _forwarded.ToString(),
            _ttftMs);
        Record(ended.Record);
    }

    private void Record(AttemptRecord record)
    {
        _recorded = true;
        _attempts?.Add(record);
    }

    private int CountCalled() => _attempts?.Count(a => a.Status != AttemptStatus.SkippedCircuitOpen) ?? 0;
}
