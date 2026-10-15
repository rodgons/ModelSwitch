using Microsoft.Extensions.Logging;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Routing.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Inference.Application;

/// <summary>Source of the uniform [0, 1) sample behind backoff jitter. Replaced in tests.</summary>
internal interface IJitterSource
{
    double Next();
}

internal sealed class RandomJitterSource : IJitterSource
{
    public double Next() => Random.Shared.NextDouble();
}

/// <summary>What the executor runs: ordered Candidates, the policy's bounds and the absolute Deadline.</summary>
/// <param name="Candidates">The Routing Decision's ordered Candidates, each with the Target to call.</param>
/// <param name="Settings">The Policy Version's execution settings.</param>
/// <param name="Deadline">When the request must be answered by: the earlier of the policy's and the client's Deadline.</param>
internal sealed record ExecutionPlan(IReadOnlyList<RankedCandidate> Candidates, ExecutionSettings Settings, DateTimeOffset Deadline);

/// <summary>
/// The Attempt loop (routing-and-resilience.md section 4, ADR-0004): walks the Candidates in order, retries transient
/// failures with backoff and jitter, honours <c>Retry-After</c> when it fits the Deadline, falls back where the failure
/// classification allows, skips Candidates whose circuit is open, and records every Attempt. It never logs or keeps content
/// (ADR-0010).
/// </summary>
internal sealed partial class AttemptExecutor(
    IAdapterFactory adapters,
    IProviderHealth health,
    IInputTokenEstimator estimator,
    IJitterSource jitter,
    TimeProvider time,
    ILogger<AttemptExecutor> logger)
{
    /// <summary>
    /// Runs the plan. Every Attempt, including skipped ones, is appended to <paramref name="attempts"/> as it happens, so the
    /// caller still has them when this throws <see cref="OperationCanceledException"/> because the client went away.
    /// Fails with <c>provider_unavailable</c>, <c>provider_error</c>, <c>context_length_exceeded</c> or <c>deadline_exceeded</c>.
    /// </summary>
    public Task<Result<CompletionOutcome.Success>> ExecuteAsync(
        ExecutionPlan plan,
        NeutralChatRequest chat,
        List<AttemptRecord> attempts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(attempts);

        return RunAsync(
            plan,
            chat,
            attempts,
            static (input, token) => input.Adapter.CompleteAsync(input.Context, input.Chat, token),
            static outcome => outcome is CompletionOutcome.Failure failure ? failure.Error : null,
            (outcome, attempt) =>
            {
                var success = (CompletionOutcome.Success)outcome;
                attempts.Add(Succeeded(attempt.Target, attempt.Kind, attempt.Number, attempt.StartedAt, success.Usage));
                return success;
            },
            cancellationToken);
    }

    /// <summary>
    /// Runs the plan for a streaming completion up to the First Token (routing-and-resilience.md section 6): retries and Fallback
    /// work exactly as for a non-streaming call until the first content chunk (or the end of an empty completion) has been
    /// received from a Provider. The returned <see cref="StreamingAttempt"/> then owns the open Provider stream and records the
    /// Attempt when it ends. From that moment there are no more retries and no Fallback: a failure is the caller's
    /// <c>stream_interrupted</c>. Nothing has been written to the client yet, so every failure here is an ordinary error.
    /// </summary>
    public Task<Result<StreamingAttempt>> ExecuteStreamAsync(
        ExecutionPlan plan,
        NeutralChatRequest chat,
        List<AttemptRecord> attempts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(attempts);

        return RunAsync(
            plan,
            chat,
            attempts,
            (input, token) => StreamingAttempt.OpenAsync(this, input, time, token),
            static start => start is StreamStart.Failed failed ? failed.Error : null,
            (start, attempt) => ((StreamStart.Started)start).Attempt.Begin(attempts, attempt),
            cancellationToken);
    }

    // The loop of routing-and-resilience.md section 4, shared by the buffered and the streaming call: only the call itself, how
    // its outcome fails, and what a success hands back differ.
    private async Task<Result<TSuccess>> RunAsync<TOutcome, TSuccess>(
        ExecutionPlan plan,
        NeutralChatRequest chat,
        List<AttemptRecord> attempts,
        Func<CallInput, CancellationToken, Task<TOutcome>> call,
        Func<TOutcome, ProviderFailure?> failureOf,
        Func<TOutcome, AttemptContext, TSuccess> succeeded,
        CancellationToken cancellationToken)
    {
        var settings = plan.Settings;
        var calls = 0;
        var deadlineReached = false;
        Error? lastError = null;

        for (var index = 0; index < plan.Candidates.Count; index++)
        {
            var candidate = plan.Candidates[index];

            // The Target comes from the configuration snapshot the Routing Decision was made on: no database read here.
            if (candidate.Target is not { } target)
            {
                continue;
            }

            if (health.StateOf(target.ProviderModelId) == CircuitState.Open)
            {
                attempts.Add(Skipped(target, KindFor(index, retry: false), attempts.Count + 1));
                LogSkipped(logger, target.ProviderModelId);
                continue;
            }

            var bound = await adapters.BindAsync(target, cancellationToken);
            if (bound.IsFailure)
            {
                lastError = bound.Error;
                continue;
            }

            for (var attempt = 0; attempt <= settings.MaxRetriesPerCandidate; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var started = time.GetUtcNow();
                if (started >= plan.Deadline)
                {
                    deadlineReached = true;
                    break;
                }

                if (calls >= settings.MaxAttempts)
                {
                    return lastError ?? new Error(ErrorCode.ProviderUnavailable, "The Provider is temporarily unavailable.");
                }

                var kind = KindFor(index, retry: attempt > 0);
                var number = attempts.Count + 1;
                var context = new AttemptContext(target, kind, number, started);

                // The Attempt timeout is the policy's, the Provider's own request timeout, or what is left of the Deadline.
                var timeout = Min(Min(TimeSpan.FromMilliseconds(settings.AttemptTimeoutMs), plan.Deadline - started), target.RequestTimeout);
                var input = new CallInput(
                    bound.Value.Adapter,
                    bound.Value.Context with { AttemptTimeout = timeout },
                    chat,
                    target,
                    timeout,
                    Min(TimeSpan.FromMilliseconds(settings.AttemptTimeoutMs), target.RequestTimeout));

                GuardedCall<TOutcome> guarded;
                try
                {
                    guarded = await health.ExecuteAsync(
                        target.ProviderModelId,
                        token => call(input, token),
                        failureOf,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The client went away. The call may have reached the Provider, so the Attempt is ambiguous, not cancelled.
                    attempts.Add(Ambiguous(chat, target, kind, number, started, ProviderErrorTypes.ClientCancelled, httpStatus: null, errorCode: null));
                    throw;
                }

                if (guarded.Rejected)
                {
                    // The circuit opened between the check and the call, or a half-open probe is already in flight.
                    attempts.Add(Skipped(target, kind, number));
                    LogSkipped(logger, target.ProviderModelId);
                    break;
                }

                calls++;
                if (failureOf(guarded.Value!) is not { } providerFailure)
                {
                    return succeeded(guarded.Value!, context);
                }

                var error = ProviderFailureMapper.ToError(providerFailure);
                lastError = error;
                attempts.Add(
                    providerFailure.Class == FailureClass.Ambiguous
                        ? Ambiguous(chat, target, kind, number, started, providerFailure.Code, providerFailure.HttpStatus, error.Code.Value)
                        : Failed(target, kind, number, started, providerFailure, error.Code.Value));

                var decision = RetryPolicy.Classify(providerFailure);
                if (!decision.RetrySameModel && !decision.Fallback)
                {
                    return error;
                }

                if (attempt < settings.MaxRetriesPerCandidate)
                {
                    var delay = RetryPolicy.DelayBeforeRetry(
                        providerFailure,
                        settings.Backoff,
                        attempt,
                        jitter.Next(),
                        plan.Deadline - time.GetUtcNow(),
                        TimeSpan.FromMilliseconds(settings.AttemptTimeoutMs));
                    if (delay is { } wait)
                    {
                        LogRetrying(logger, target.ProviderModelId, providerFailure.Code, (int)wait.TotalMilliseconds);
                        await Task.Delay(wait, time, cancellationToken);
                        continue;
                    }
                }

                // A failure that is neither retried here nor stopped above goes to the next Candidate.
                break;
            }

            if (deadlineReached)
            {
                break;
            }
        }

        if (deadlineReached)
        {
            return new Error(ErrorCode.DeadlineExceeded, "The Deadline was reached before a Provider answered.");
        }

        // Nothing was called: every circuit was open (recorded as skipped Attempts) or no Candidate is usable any more.
        return lastError ?? (attempts.Count > 0
            ? new Error(ErrorCode.ProviderUnavailable, "The Provider is temporarily unavailable.")
            : new Error(ErrorCode.NoEligibleModel, "No eligible model can serve this request."));
    }

    /// <summary>What one call of the loop needs: the bound Adapter and its context, the request and the time limits.</summary>
    /// <param name="Adapter">The bound Adapter.</param>
    /// <param name="Context">Its call context, carrying <paramref name="AttemptTimeout"/>.</param>
    /// <param name="Chat">The request, with the policy's output ceiling applied.</param>
    /// <param name="Target">The Provider Model being called.</param>
    /// <param name="AttemptTimeout">The smallest of the policy's, the Provider's and what is left of the Deadline.</param>
    /// <param name="StallTimeout">How long a started stream may stay silent before it counts as stalled.</param>
    internal readonly record struct CallInput(
        IProviderAdapter Adapter,
        AdapterContext Context,
        NeutralChatRequest Chat,
        ProviderModelTarget Target,
        TimeSpan AttemptTimeout,
        TimeSpan StallTimeout);

    /// <summary>Which Attempt this is: the Candidate, why it was made, its number and when it began.</summary>
    internal readonly record struct AttemptContext(ProviderModelTarget Target, AttemptKind Kind, int Number, DateTimeOffset StartedAt);

    private static AttemptKind KindFor(int candidateIndex, bool retry)
        => retry ? AttemptKind.Retry : candidateIndex == 0 ? AttemptKind.Primary : AttemptKind.Fallback;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;

    private AttemptRecord Skipped(ProviderModelTarget target, AttemptKind kind, int number)
    {
        var now = time.GetUtcNow();
        return new AttemptRecord(
            number,
            target.ProviderId,
            target.ProviderModelId,
            kind,
            AttemptStatus.SkippedCircuitOpen,
            ErrorClass: null,
            ErrorCode: null,
            HttpStatus: null,
            ProviderErrorType: null,
            now,
            now,
            TtftMs: null,
            InputTokens: null,
            OutputTokens: null,
            UsageSource.Reported);
    }

    private AttemptRecord Succeeded(ProviderModelTarget target, AttemptKind kind, int number, DateTimeOffset startedAt, NeutralUsage? usage)
        => Record(
            target,
            kind,
            number,
            startedAt,
            AttemptStatus.Succeeded,
            errorClass: null,
            errorCode: null,
            httpStatus: null,
            providerErrorType: null,
            inputTokens: usage?.InputTokens,
            outputTokens: usage?.OutputTokens,
            usage is null ? UsageSource.Unavailable : UsageSource.Reported);

    // A definite failure cost nothing: the Provider refused the call, so zero tokens are the known truth, not a guess.
    private AttemptRecord Failed(ProviderModelTarget target, AttemptKind kind, int number, DateTimeOffset startedAt, ProviderFailure failure, string errorCode)
        => Record(
            target,
            kind,
            number,
            startedAt,
            AttemptStatus.Failed,
            failure.Class == FailureClass.Transient ? AttemptErrorClass.Transient : AttemptErrorClass.Permanent,
            errorCode,
            failure.HttpStatus,
            failure.Code,
            inputTokens: 0,
            outputTokens: 0,
            UsageSource.Reported);

    // Ambiguous: possibly billed. Input is the estimate and output is unknown (usage-and-budgets.md section 4).
    private AttemptRecord Ambiguous(NeutralChatRequest chat, ProviderModelTarget target, AttemptKind kind, int number, DateTimeOffset startedAt, string providerErrorType, int? httpStatus, string? errorCode)
        => Record(
            target,
            kind,
            number,
            startedAt,
            AttemptStatus.Ambiguous,
            AttemptErrorClass.Ambiguous,
            errorCode,
            httpStatus,
            providerErrorType,
            inputTokens: estimator.Estimate([.. chat.Messages.Select(m => m.Text)], target.Tokenizer).Tokens,
            outputTokens: null,
            UsageSource.Unavailable);

    private AttemptRecord Record(
        ProviderModelTarget target,
        AttemptKind kind,
        int number,
        DateTimeOffset startedAt,
        AttemptStatus status,
        AttemptErrorClass? errorClass,
        string? errorCode,
        int? httpStatus,
        string? providerErrorType,
        int? inputTokens,
        int? outputTokens,
        UsageSource usageSource)
        => new(
            number,
            target.ProviderId,
            target.ProviderModelId,
            kind,
            status,
            errorClass,
            errorCode,
            httpStatus,
            providerErrorType,
            startedAt,
            time.GetUtcNow(),
            TtftMs: null,
            inputTokens,
            outputTokens,
            usageSource);

    /// <summary>
    /// The Attempt record of a stream that has started (the First Token went out). Usage is the Provider's when it reported any;
    /// otherwise input tokens are estimated from the request and output tokens counted from the text forwarded, with the
    /// model's tokenizer, and the source is <c>estimated</c> (routing-and-resilience.md section 6).
    /// </summary>
    internal (AttemptRecord Record, NeutralUsage Usage, UsageSource Source) EndStream(
        NeutralChatRequest chat,
        AttemptContext attempt,
        AttemptStatus status,
        AttemptErrorClass? errorClass,
        string? errorCode,
        int? httpStatus,
        string? providerErrorType,
        NeutralUsage? reported,
        string forwarded,
        int? ttftMs)
    {
        var target = attempt.Target;
        NeutralUsage usage;
        UsageSource source;
        if (reported is not null)
        {
            usage = reported;
            source = UsageSource.Reported;
        }
        else
        {
            var input = estimator.Estimate([.. chat.Messages.Select(m => m.Text)], target.Tokenizer).Tokens;
            var output = forwarded.Length == 0 ? 0 : Math.Max(0, estimator.Estimate([forwarded], target.Tokenizer).Tokens - IInputTokenEstimator.PerMessageOverhead);
            usage = new NeutralUsage(input, output, null);
            source = UsageSource.Estimated;
        }

        var now = time.GetUtcNow();
        var record = new AttemptRecord(
            attempt.Number,
            target.ProviderId,
            target.ProviderModelId,
            attempt.Kind,
            status,
            errorClass,
            errorCode,
            httpStatus,
            providerErrorType,
            attempt.StartedAt,
            now,
            ttftMs,
            usage.InputTokens,
            usage.OutputTokens,
            source);
        return (record, usage, source);
    }

    /// <summary>
    /// A failure after the First Token still says something about the Provider, so the circuit is told. The call that opened the
    /// stream already counted as a success, which is all the breaker could know then. A breaker that is open meanwhile ignores it.
    /// </summary>
    internal async Task ReportStreamFailureAsync(Guid providerModelId, ProviderFailure failure)
        => await health.ExecuteAsync(providerModelId, _ => Task.FromResult(failure), static f => f, CancellationToken.None);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping Provider Model {ProviderModelId}: its circuit is open.")]
    private static partial void LogSkipped(ILogger logger, Guid providerModelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Retrying Provider Model {ProviderModelId} after {FailureCode} in {DelayMs} ms.")]
    private static partial void LogRetrying(ILogger logger, Guid providerModelId, string failureCode, int delayMs);

    internal static class ProviderErrorTypes
    {
        public const string ClientCancelled = "client_cancelled";
    }
}
