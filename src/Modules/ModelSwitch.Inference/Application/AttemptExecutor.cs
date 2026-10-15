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
    public async Task<Result<CompletionOutcome.Success>> ExecuteAsync(
        ExecutionPlan plan,
        NeutralChatRequest chat,
        List<AttemptRecord> attempts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(attempts);

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

                // The Attempt timeout is the policy's, the Provider's own request timeout, or what is left of the Deadline.
                var timeout = Min(Min(TimeSpan.FromMilliseconds(settings.AttemptTimeoutMs), plan.Deadline - started), target.RequestTimeout);
                var context = bound.Value.Context with { AttemptTimeout = timeout };
                var adapter = bound.Value.Adapter;

                GuardedCall<CompletionOutcome> guarded;
                try
                {
                    guarded = await health.ExecuteAsync(
                        target.ProviderModelId,
                        token => adapter.CompleteAsync(context, chat, token),
                        static outcome => outcome is CompletionOutcome.Failure failure ? failure.Error : null,
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
                switch (guarded.Value)
                {
                    case CompletionOutcome.Success success:
                        attempts.Add(Succeeded(target, kind, number, started, success.Usage));
                        return success;

                    case CompletionOutcome.Failure failure:
                        var error = ProviderFailureMapper.ToError(failure.Error);
                        lastError = error;
                        attempts.Add(
                            failure.Error.Class == FailureClass.Ambiguous
                                ? Ambiguous(chat, target, kind, number, started, failure.Error.Code, failure.Error.HttpStatus, error.Code.Value)
                                : Failed(target, kind, number, started, failure.Error, error.Code.Value));

                        var decision = RetryPolicy.Classify(failure.Error);
                        if (!decision.RetrySameModel && !decision.Fallback)
                        {
                            return error;
                        }

                        if (attempt < settings.MaxRetriesPerCandidate)
                        {
                            var delay = RetryPolicy.DelayBeforeRetry(
                                failure.Error,
                                settings.Backoff,
                                attempt,
                                jitter.Next(),
                                plan.Deadline - time.GetUtcNow(),
                                TimeSpan.FromMilliseconds(settings.AttemptTimeoutMs));
                            if (delay is { } wait)
                            {
                                LogRetrying(logger, target.ProviderModelId, failure.Error.Code, (int)wait.TotalMilliseconds);
                                await Task.Delay(wait, time, cancellationToken);
                                continue;
                            }
                        }

                        break;

                    default:
                        return new Error(ErrorCode.InternalError, "An internal error occurred.");
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

        // Nothing was called: every circuit was open (recorded as skipped Attempts) or no Candidate is enabled any more.
        return lastError ?? (attempts.Count > 0
            ? new Error(ErrorCode.ProviderUnavailable, "The Provider is temporarily unavailable.")
            : new Error(ErrorCode.NoEligibleModel, "No eligible model can serve this request."));
    }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping Provider Model {ProviderModelId}: its circuit is open.")]
    private static partial void LogSkipped(ILogger logger, Guid providerModelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Retrying Provider Model {ProviderModelId} after {FailureCode} in {DelayMs} ms.")]
    private static partial void LogRetrying(ILogger logger, Guid providerModelId, string failureCode, int delayMs);

    private static class ProviderErrorTypes
    {
        public const string ClientCancelled = "client_cancelled";
    }
}
