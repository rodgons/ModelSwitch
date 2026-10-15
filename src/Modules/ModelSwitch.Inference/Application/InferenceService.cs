using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Inference.Application;

/// <summary>
/// Phase 1 stub of <see cref="IInferenceService"/>: resolves the alias from <see cref="InferenceOptions.StaticAliases"/>,
/// binds one Adapter and makes exactly one Attempt. Temporary until Phase 2 replaces the alias lookup with Routing and
/// adds the Attempt loop and limits. It does record the request: one <c>inference_request</c> opened before the Attempt
/// and settled after it, with the Attempt(s) in between, so the history is the same shape the full executor will write.
/// It never logs or persists content (ADR-0010).
/// </summary>
internal sealed partial class InferenceService(
    InferenceOptions options,
    IProviderCatalog catalog,
    IAdapterFactory adapters,
    IUsageLedger ledger,
    IInputTokenEstimator estimator,
    TimeProvider time,
    ILogger<InferenceService> logger) : IInferenceService
{
    /// <summary>Settlement is bounded and independent of the client (architecture.md section 5, usage-and-budgets.md section 3).</summary>
    internal static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan[] _settleBackoff = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(1.6)];

    public async Task<Result<InferenceResult>> CompleteAsync(InferenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var opened = await ledger.OpenAsync(
            new OpenInferenceRequest(
                request.RequestId,
                request.TenantId,
                request.Origin,
                request.ApiKeyId,
                request.EvaluationRunId,
                request.ClientRequestId,
                request.Alias,
                TaskType: null,
                Streaming: false,
                request.InputBytes,
                Activity.Current?.TraceId.ToString() ?? string.Empty),
            cancellationToken);
        if (opened.IsFailure)
        {
            return opened.Error;
        }

        var attempts = new List<AttemptRecord>();
        Result<InferenceResult> result;
        try
        {
            result = await ExecuteAsync(request, attempts, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client went away. The Attempt in flight may have reached the Provider, so it is ambiguous, not cancelled.
            await SettleAsync(request, opened.Value, RequestOutcome.Cancelled, errorCode: null, attempts);
            throw;
        }
        catch
        {
            // A bug or infrastructure fault must not leave the request 'running'; the failure itself still propagates.
            await SettleAsync(request, opened.Value, RequestOutcome.Failed, ErrorCode.InternalError.Value, attempts);
            throw;
        }

        await SettleAsync(
            request,
            opened.Value,
            result.IsSuccess ? RequestOutcome.Succeeded : RequestOutcome.Failed,
            result.IsSuccess ? null : result.Error.Code.Value,
            attempts);
        return result;
    }

    private async Task<Result<InferenceResult>> ExecuteAsync(InferenceRequest request, List<AttemptRecord> attempts, CancellationToken cancellationToken)
    {
        var binding = options.StaticAliases.FirstOrDefault(a => string.Equals(a.Alias, request.Alias, StringComparison.Ordinal));
        if (binding is null)
        {
            return new Error(ErrorCode.ModelNotFound, "The requested model does not exist.");
        }

        var target = await catalog.FindEnabledAsync(binding.ProviderName, binding.ProviderModelIdentifier, cancellationToken);
        if (target is null)
        {
            return new Error(ErrorCode.NoEligibleModel, "No eligible model can serve this request.");
        }

        var bound = await adapters.BindAsync(target, cancellationToken);
        if (bound.IsFailure)
        {
            return bound.Error;
        }

        var startedAt = time.GetUtcNow();
        CompletionOutcome outcome;
        try
        {
            outcome = await bound.Value.Adapter.CompleteAsync(bound.Value.Context, request.Chat, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            attempts.Add(Ambiguous(request, target, startedAt, ProviderErrorTypes.ClientCancelled, httpStatus: null, errorCode: null));
            throw;
        }

        switch (outcome)
        {
            case CompletionOutcome.Success success:
                attempts.Add(Succeeded(target, startedAt, success.Usage));
                return new InferenceResult(request.RequestId, request.Alias, success.Text, success.FinishReason, success.Usage, 1);

            case CompletionOutcome.Failure failure:
                var error = ProviderFailureMapper.ToError(failure.Error);
                attempts.Add(
                    failure.Error.Class == FailureClass.Ambiguous
                        ? Ambiguous(request, target, startedAt, failure.Error.Code, failure.Error.HttpStatus, error.Code.Value)
                        : Failed(target, startedAt, failure.Error, error.Code.Value));
                return error;

            default:
                return new Error(ErrorCode.InternalError, "An internal error occurred.");
        }
    }

    private AttemptRecord Succeeded(ProviderModelTarget target, DateTimeOffset startedAt, NeutralUsage? usage)
        => Record(
            target,
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
    private AttemptRecord Failed(ProviderModelTarget target, DateTimeOffset startedAt, ProviderFailure failure, string errorCode)
        => Record(
            target,
            startedAt,
            AttemptStatus.Failed,
            failure.Class == FailureClass.Transient ? AttemptErrorClass.Transient : AttemptErrorClass.Permanent,
            errorCode,
            failure.HttpStatus,
            failure.Code,
            inputTokens: 0,
            outputTokens: 0,
            UsageSource.Reported);

    // Ambiguous: possibly billed. Input is the estimate and output is unknown (usage-and-budgets.md section 4). The Phase 1
    // target carries no tokenizer name, so the pessimistic byte estimate applies until the catalog snapshot supplies one.
    private AttemptRecord Ambiguous(InferenceRequest request, ProviderModelTarget target, DateTimeOffset startedAt, string providerErrorType, int? httpStatus, string? errorCode)
        => Record(
            target,
            startedAt,
            AttemptStatus.Ambiguous,
            AttemptErrorClass.Ambiguous,
            errorCode,
            httpStatus,
            providerErrorType,
            inputTokens: estimator.Estimate([.. request.Chat.Messages.Select(m => m.Text)], tokenizer: null).Tokens,
            outputTokens: null,
            UsageSource.Unavailable);

    private AttemptRecord Record(
        ProviderModelTarget target,
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
            AttemptNumber: 1,
            target.ProviderId,
            target.ProviderModelId,
            AttemptKind.Primary,
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
    /// Step 11. Runs on its own short-timeout token, never the client's, so an aborted request is still recorded. Retried
    /// with backoff inside that window; if it still fails the request stays 'running' for the sweeper (Phase 3), and the
    /// client is not made to wait or fail for it.
    /// </summary>
    private async Task SettleAsync(InferenceRequest request, OpenedRequest opened, RequestOutcome outcome, string? errorCode, List<AttemptRecord> attempts)
    {
        var settlement = new SettleInferenceRequest(
            opened.RequestId,
            opened.StartedAt,
            outcome,
            errorCode,
            PolicyId: null,
            PolicyVersion: null,
            RoutingDecisionJson: null,
            TtftMs: null,
            attempts);

        using var timeout = new CancellationTokenSource(SettleTimeout, time);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await ledger.SettleAsync(settlement, timeout.Token);
                return;
            }
#pragma warning disable CA1031 // Any storage fault is retried, then reported; settlement must never fail the response.
            catch (Exception exception) when (!timeout.IsCancellationRequested)
#pragma warning restore CA1031
            {
                LogSettleFailed(logger, exception, request.RequestId, attempt + 1);
                if (attempt >= _settleBackoff.Length)
                {
                    LogSettleGaveUp(logger, request.RequestId);
                    return;
                }

                try
                {
                    await Task.Delay(_settleBackoff[attempt], time, timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    LogSettleGaveUp(logger, request.RequestId);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                LogSettleGaveUp(logger, request.RequestId);
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settling request {RequestId} failed (try {Try}); retrying.")]
    private static partial void LogSettleFailed(ILogger logger, Exception exception, Guid requestId, int @try);

    [LoggerMessage(Level = LogLevel.Error, Message = "Settling request {RequestId} timed out; it stays 'running' until the sweeper recovers it.")]
    private static partial void LogSettleGaveUp(ILogger logger, Guid requestId);

    private static class ProviderErrorTypes
    {
        public const string ClientCancelled = "client_cancelled";
    }
}
