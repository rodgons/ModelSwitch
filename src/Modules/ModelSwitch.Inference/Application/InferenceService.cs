using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Routing.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Inference.Application;

/// <summary>
/// <see cref="IInferenceService"/> with a static candidate source: the alias is resolved from
/// <see cref="InferenceOptions.StaticAliases"/> into an ordered Candidate list (temporary, until Routing and the runtime
/// configuration snapshot replace it) and run by the <see cref="AttemptExecutor"/>. The request is recorded: one
/// <c>inference_request</c> opened before the first Attempt and settled after the last, with every Attempt in between.
/// It never logs or persists content (ADR-0010).
/// </summary>
internal sealed partial class InferenceService(
    InferenceOptions options,
    IProviderCatalog catalog,
    AttemptExecutor executor,
    IUsageLedger ledger,
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
        var received = time.GetUtcNow();
        var binding = options.StaticAliases.FirstOrDefault(a => string.Equals(a.Alias, request.Alias, StringComparison.Ordinal));
        if (binding is null)
        {
            return new Error(ErrorCode.ModelNotFound, "The requested model does not exist.");
        }

        var candidates = new List<RankedCandidate>();
        foreach (var (providerName, identifier) in binding.Targets)
        {
            // Skipped, not fatal, when disabled or unknown: the next Candidate can still serve the request.
            var target = await catalog.FindEnabledAsync(providerName, identifier, cancellationToken);
            if (target is not null)
            {
                candidates.Add(new RankedCandidate(target.ProviderModelId, target.ProviderId, target.ProviderName, target.ProviderModelIdentifier, WorstCaseEstimatedCost: 0m));
            }
        }

        if (candidates.Count == 0)
        {
            return new Error(ErrorCode.NoEligibleModel, "No eligible model can serve this request.");
        }

        var settings = ExecutionSettings.Default;
        var deadline = received + TimeSpan.FromMilliseconds(settings.DeadlineMs);
        if (request.Timeout is { } requested && received + requested < deadline)
        {
            deadline = received + requested;
        }

        var executed = await executor.ExecuteAsync(new ExecutionPlan(candidates, settings, deadline), request.Chat, attempts, cancellationToken);
        if (executed.IsFailure)
        {
            return executed.Error;
        }

        var success = executed.Value;
        return new InferenceResult(request.RequestId, request.Alias, success.Text, success.FinishReason, success.Usage, attempts.Count(a => a.Status != AttemptStatus.SkippedCircuitOpen));
    }

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
}
