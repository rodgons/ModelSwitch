using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Configuration.Contracts;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Routing.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Inference.Application;

/// <summary>
/// <see cref="IInferenceService"/>: reads the configuration snapshot once, asks <see cref="IRouter"/> for the ordered
/// Candidates, and runs them through the <see cref="AttemptExecutor"/> under the Policy Version's execution settings. The
/// request is recorded: one <c>inference_request</c> opened (with its Budget Reservation) once routing has chosen the Candidates, and settled after the last Attempt, with the
/// Policy Version, the Routing Decision and every Attempt. It never logs or persists content (ADR-0010).
/// </summary>
internal sealed partial class InferenceService(
    IConfigurationSnapshotProvider configuration,
    IRouter router,
    IInputTokenEstimator estimator,
    IProviderHealth health,
    AttemptExecutor executor,
    IUsageLedger ledger,
    TimeProvider time,
    ILogger<InferenceService> logger) : IInferenceService
{
    /// <summary>Settlement is bounded and independent of the client (architecture.md section 5, usage-and-budgets.md section 3).</summary>
    internal static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan[] _settleBackoff = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(1.6)];

    // The distinct tokenizers a snapshot's models name never change within a snapshot, so they are worked out once per snapshot.
    private static readonly ConditionalWeakTable<ConfigurationSnapshot, string[]> _tokenizers = [];

    /// <summary>What routing decided, kept for the settlement: the Policy Version and the Routing Decision.</summary>
    private sealed record Routed(
        Guid? PolicyId,
        int? PolicyVersion,
        string? DecisionJson,
        Error? Error,
        ExecutionPlan? Plan,
        NeutralChatRequest? Chat,
        RoutingSnapshot? Catalog = null);

    /// <summary>How long after the Deadline a held Reservation survives before the sweeper may treat the request as abandoned.</summary>
    internal static readonly TimeSpan ReservationGrace = TimeSpan.FromMinutes(1);

    /// <summary>A stream is bounded only by the Provider and the client once it has started, so its Reservation expires much later.</summary>
    internal static readonly TimeSpan StreamReservationGrace = TimeSpan.FromMinutes(15);

    public async Task<Result<InferenceResult>> CompleteAsync(InferenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var received = time.GetUtcNow();
        var routed = Route(request, received, streaming: false);
        var opened = await OpenAsync(request, routed, streaming: false, received, cancellationToken);
        if (opened.IsFailure)
        {
            return opened.Error;
        }

        routed = opened.Value.Routed;
        var attempts = new List<AttemptRecord>();
        Result<InferenceResult> result;
        try
        {
            result = await ExecuteAsync(request, routed, attempts, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client went away. The Attempt in flight may have reached the Provider, so it is ambiguous, not cancelled.
            await SettleAsync(request, opened.Value.Request, routed, RequestOutcome.Cancelled, errorCode: null, ttftMs: null, attempts);
            throw;
        }
        catch
        {
            // A bug or infrastructure fault must not leave the request 'running'; the failure itself still propagates.
            await SettleAsync(request, opened.Value.Request, routed, RequestOutcome.Failed, ErrorCode.InternalError.Value, ttftMs: null, attempts);
            throw;
        }

        await SettleAsync(
            request,
            opened.Value.Request,
            routed,
            result.IsSuccess ? RequestOutcome.Succeeded : RequestOutcome.Failed,
            result.IsSuccess ? null : result.Error.Code.Value,
            ttftMs: null,
            attempts);
        return result;
    }

    private async Task<Result<InferenceResult>> ExecuteAsync(InferenceRequest request, Routed routed, List<AttemptRecord> attempts, CancellationToken cancellationToken)
    {
        if (routed.Plan is not { } plan)
        {
            return routed.Error!;
        }

        var executed = await executor.ExecuteAsync(plan, routed.Chat!, attempts, cancellationToken);
        if (executed.IsFailure)
        {
            return WithRetryAfter(executed.Error, plan, attempts);
        }

        var success = executed.Value;
        return new InferenceResult(request.RequestId, request.Alias, success.Text, success.FinishReason, success.Usage, attempts.Count(a => a.Status != AttemptStatus.SkippedCircuitOpen));
    }

    /// <summary>
    /// Step 7. A pure function of the snapshot taken here: the same instance serves the whole request, so a configuration
    /// change in between never mixes two Policy Versions. Routing failures keep their Decision for the record.
    /// </summary>
    private Routed Route(InferenceRequest request, DateTimeOffset received, bool streaming)
    {
        var snapshot = configuration.Current;
        var texts = ChatInput.Texts(request.Chat);
        var input = new RoutingInput(
            request.TenantId,
            request.Alias,
            snapshot.AccessFor(request.TenantId),
            request.TaskType,
            RequiredCapabilities(request, streaming),
            EstimateInput(snapshot, texts),
            request.Chat.MaxOutputTokens,
            snapshot.Routing,
            health.OpenCircuits());

        var outcome = router.Decide(input);
        var decision = outcome.Decision;
        var json = decision is null ? null : RoutingDecisionJson.Write(decision);
        if (outcome.Error is not null || decision is null)
        {
            return new Routed(decision?.PolicyId, decision?.PolicyVersion, json, outcome.Error! with { RetryAfter = outcome.RetryAfter }, null, null, snapshot.Routing);
        }

        var settings = decision.Execution;
        var deadline = received + TimeSpan.FromMilliseconds(settings.DeadlineMs);
        if (request.Timeout is { } requested && received + requested < deadline)
        {
            deadline = received + requested;
        }

        // The Reservation and the Routing Decision were costed at this ceiling, so the Provider is held to it.
        var chat = request.Chat with { MaxOutputTokens = decision.EffectiveMaxOutputTokens };
        return new Routed(decision.PolicyId, decision.PolicyVersion, json, null, new ExecutionPlan(decision.Ordered, settings, deadline), chat, snapshot.Routing);
    }

    private static List<string> RequiredCapabilities(InferenceRequest request, bool streaming)
    {
        var required = new List<string>(2);
        if (streaming)
        {
            required.Add(CapabilityNames.Streaming);
        }

        if (request.Chat.ResponseFormat is ResponseFormat.JsonObject)
        {
            required.Add(CapabilityNames.JsonObject);
        }
        else if (request.Chat.ResponseFormat is ResponseFormat.JsonSchema)
        {
            required.Add(CapabilityNames.JsonSchema);
        }

        return required;
    }

    // One estimate per tokenizer the snapshot's models declare (routing-and-resilience.md section 1); the byte estimate covers the rest.
    private Routing.Contracts.InputTokenEstimate EstimateInput(ConfigurationSnapshot snapshot, IReadOnlyList<string> texts)
    {
        var tokenizers = _tokenizers.GetValue(snapshot, static s => s.Tokenizers());
        var byTokenizer = new Dictionary<string, int>(tokenizers.Length, StringComparer.Ordinal);
        foreach (var tokenizer in tokenizers)
        {
            byTokenizer[tokenizer] = estimator.Estimate(texts, tokenizer).Tokens;
        }

        return new Routing.Contracts.InputTokenEstimate(byTokenizer, estimator.Estimate(texts, tokenizer: null).Tokens);
    }

    // When nothing could be called because every circuit is open, the client is told when the first one lets a probe through.
    private Error WithRetryAfter(Error error, ExecutionPlan plan, List<AttemptRecord> attempts)
    {
        if (error.Code != ErrorCode.ProviderUnavailable || attempts.Count == 0 || attempts.Any(a => a.Status != AttemptStatus.SkippedCircuitOpen))
        {
            return error;
        }

        var open = health.OpenCircuits();
        TimeSpan? shortest = null;
        foreach (var candidate in plan.Candidates)
        {
            if (open.TryGetValue(candidate.ProviderModelId, out var remaining) && (shortest is null || remaining < shortest))
            {
                shortest = remaining;
            }
        }

        return shortest is null ? error : error with { RetryAfter = shortest };
    }

    /// <summary>
    /// Step 8: opens the request and reserves the worst case of the most expensive Candidate times <c>min(maxAttempts, 2)</c>, so a
    /// Fallback normally needs no second round trip. A refusal is <c>budget_exceeded</c> (402).
    /// </summary>
    private async Task<Result<(OpenedRequest Request, Routed Routed)>> OpenAsync(InferenceRequest request, Routed routed, bool streaming, DateTimeOffset received, CancellationToken cancellationToken)
    {
        var hold = Money.Zero;
        var expiresAt = received + (streaming ? StreamReservationGrace : ReservationGrace);
        if (routed.Plan is { } plan)
        {
            var worst = plan.Candidates.Max(c => c.WorstCaseEstimatedCost);
            hold = Money.FromUsd(worst * Math.Min(plan.Settings.MaxAttempts, 2)).Round();
            expiresAt = plan.Deadline + (streaming ? StreamReservationGrace : ReservationGrace);
        }

        var opened = await ledger.OpenAsync(
            new OpenInferenceRequest(
                request.RequestId,
                request.TenantId,
                request.Origin,
                request.ApiKeyId,
                request.EvaluationRunId,
                request.ClientRequestId,
                request.Alias,
                request.TaskType,
                streaming,
                request.InputBytes,
                Activity.Current?.TraceId.ToString() ?? string.Empty,
                new ReservationRequest(hold, expiresAt)),
            cancellationToken);
        if (opened.IsFailure)
        {
            return opened.Error;
        }

        if (routed.Plan is { } routedPlan)
        {
            routed = routed with { Plan = routedPlan with { Budget = new AttemptBudget(ledger, request.RequestId, hold) } };
        }

        return (opened.Value, routed);
    }

    private static AttemptPricing? PricingOf(Routed routed, Guid providerModelId)
        => routed.Catalog is { } catalog && catalog.Models.TryGetValue(providerModelId, out var model) && model.Price is { } price
            ? new AttemptPricing(price.ModelPriceId, new PriceRates(price.InputPerMillion, price.CachedInputPerMillion, price.OutputPerMillion))
            : null;

    /// <summary>
    /// Step 11. Runs on its own short-timeout token, never the client's, so an aborted request is still recorded. Retried
    /// with backoff inside that window; if it still fails the request stays 'running' for the sweeper (Phase 3), and the
    /// client is not made to wait or fail for it.
    /// </summary>
    private async Task SettleAsync(InferenceRequest request, OpenedRequest opened, Routed routed, RequestOutcome outcome, string? errorCode, int? ttftMs, List<AttemptRecord> attempts)
    {
        var settlement = new SettleInferenceRequest(
            opened.RequestId,
            opened.StartedAt,
            outcome,
            errorCode,
            routed.PolicyId,
            routed.PolicyVersion,
            routed.DecisionJson,
            ttftMs,
            [.. attempts.Select(a => a with { Pricing = PricingOf(routed, a.ProviderModelId) })],
            routed.Chat?.MaxOutputTokens);

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
