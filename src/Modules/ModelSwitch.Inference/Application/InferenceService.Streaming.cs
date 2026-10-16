using System.Runtime.CompilerServices;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Inference.Application;

/// <summary>The streaming half of <see cref="InferenceService"/> (api.md section 1, routing-and-resilience.md section 6).</summary>
internal sealed partial class InferenceService
{
    public async Task<Result<StreamingCompletion>> StreamAsync(InferenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var received = time.GetUtcNow();

        var routed = Route(request, received, streaming: true);
        var opened = await OpenAsync(request, routed, streaming: true, received, cancellationToken);
        if (opened.IsFailure)
        {
            return opened.Error;
        }

        routed = opened.Value.Routed;
        var attempts = new List<AttemptRecord>();
        Result<StreamingAttempt> started;
        try
        {
            started = routed.Plan is { } plan
                ? await executor.ExecuteStreamAsync(plan, routed.Chat!, attempts, cancellationToken)
                : routed.Error!;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await SettleAsync(request, opened.Value.Request, routed, RequestOutcome.Cancelled, errorCode: null, ttftMs: null, attempts);
            throw;
        }
        catch
        {
            await SettleAsync(request, opened.Value.Request, routed, RequestOutcome.Failed, ErrorCode.InternalError.Value, ttftMs: null, attempts);
            throw;
        }

        if (started.IsFailure)
        {
            // Before the First Token the client has seen nothing, so this is an ordinary error response.
            var error = routed.Plan is { } failedPlan ? WithRetryAfter(started.Error, failedPlan, attempts) : started.Error;
            await SettleAsync(request, opened.Value.Request, routed, RequestOutcome.Failed, error.Code.Value, ttftMs: null, attempts);
            return error;
        }

        var run = new StreamRun(this, time, request, opened.Value.Request, routed, attempts, started.Value, received);
        return new StreamingCompletion(request.RequestId, request.Alias, run.ReadAsync, run.AbandonAsync);
    }

    /// <summary>
    /// One running stream: forwards the Attempt's parts and settles the request exactly once, whichever way it ends. The
    /// settlement of a finished stream runs after its last part has been handed over, so the client is not kept waiting for the database.
    /// </summary>
    private sealed class StreamRun(
        InferenceService service,
        TimeProvider time,
        InferenceRequest request,
        OpenedRequest opened,
        Routed routed,
        List<AttemptRecord> attempts,
        StreamingAttempt attempt,
        DateTimeOffset received)
    {
        private int _settled;
        private int? _ttftMs;

        public async IAsyncEnumerable<StreamPart> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            StreamPart? terminal = null;
            var enumerator = attempt.ReadAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    StreamPart part;
                    try
                    {
                        if (!await enumerator.MoveNextAsync())
                        {
                            break;
                        }

                        part = enumerator.Current;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // The client went away mid-stream: the Provider was already generating, so the Attempt is ambiguous.
                        await SettleOnceAsync(RequestOutcome.Cancelled, errorCode: null);
                        throw;
                    }
                    catch
                    {
                        await SettleOnceAsync(RequestOutcome.Failed, ErrorCode.InternalError.Value);
                        throw;
                    }

                    if (part is StreamPart.Content && _ttftMs is null)
                    {
                        _ttftMs = (int)Math.Clamp((time.GetUtcNow() - received).TotalMilliseconds, 0, int.MaxValue);
                    }

                    if (part is StreamPart.Completed or StreamPart.Interrupted)
                    {
                        terminal = part;
                    }

                    yield return part;
                }

                // The last part has been delivered. Now record the outcome.
                switch (terminal)
                {
                    case StreamPart.Completed:
                        await SettleOnceAsync(RequestOutcome.Succeeded, errorCode: null);
                        break;
                    case StreamPart.Interrupted interrupted:
                        await SettleOnceAsync(RequestOutcome.Failed, interrupted.Error.Code.Value);
                        break;
                }
            }
            finally
            {
                // A consumer that stops early (the client can no longer be written to) lands here with nothing settled.
                await enumerator.DisposeAsync();
                await SettleOnceAsync(RequestOutcome.Cancelled, errorCode: null);
            }
        }

        public async ValueTask AbandonAsync()
        {
            await attempt.AbandonAsync();
            await SettleOnceAsync(RequestOutcome.Cancelled, errorCode: null);
        }

        private async ValueTask SettleOnceAsync(RequestOutcome outcome, string? errorCode)
        {
            if (Interlocked.Exchange(ref _settled, 1) == 0)
            {
                await service.SettleAsync(request, opened, routed, outcome, errorCode, _ttftMs, attempts);
            }
        }
    }
}
