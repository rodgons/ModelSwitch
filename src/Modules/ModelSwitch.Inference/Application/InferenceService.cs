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
using ModelSwitch.SharedKernel.Telemetry;
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
    IPolicyStore policies,
    IInputTokenEstimator estimator,
    IProviderHealth health,
    AttemptExecutor executor,
    IUsageLedger ledger,
    ICostCalculator costs,
    InferenceMetrics metrics,
    LimitsMetrics limits,
    TimeProvider time,
    ILogger<InferenceService> logger) : IInferenceService
{
    /// <summary>Settlement is bounded and independent of the client (architecture.md section 5, usage-and-budgets.md section 3).</summary>
    internal static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan[] _settleBackoff = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(1.6)];

    // The distinct tokenizers a snapshot's models name never change within a snapshot, so they are worked out once per snapshot.
    private static readonly ConditionalWeakTable<ConfigurationSnapshot, string[]> _tokenizers = [];

    /// <summary>
    /// What routing decided, kept for the settlement: the Policy Version and the Routing Decision. <see cref="RequestedAlias"/> is what
    /// the request is recorded and echoed under: the alias, or <c>provider/model</c> for a pinned Provider Model.
    /// </summary>
    private sealed record Routed(
        string RequestedAlias,
        Guid? PolicyId,
        int? PolicyVersion,
        string? DecisionJson,
        Error? Error,
        ExecutionPlan? Plan,
        NeutralChatRequest? Chat,
        RoutingSnapshot? Catalog = null)
    {
        public bool Streaming { get; init; }
    }

    /// <summary>How long after the Deadline a held Reservation survives before the sweeper may treat the request as abandoned.</summary>
    internal static readonly TimeSpan ReservationGrace = TimeSpan.FromMinutes(1);

    /// <summary>A stream is bounded only by the Provider and the client once it has started, so its Reservation expires much later.</summary>
    internal static readonly TimeSpan StreamReservationGrace = TimeSpan.FromMinutes(15);

    public async Task<Result<InferenceResult>> CompleteAsync(InferenceRequest request, CancellationToken cancellationToken)
    {
        CheckOrigin(request);

        var received = time.GetUtcNow();
        var routed = await RouteAsync(request, received, streaming: false, cancellationToken);
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
        return result.IsSuccess ? WithAccounting(result.Value, routed, attempts, received) : result;
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
        return new InferenceResult(request.RequestId, routed.RequestedAlias, success.Text, success.FinishReason, success.Usage, attempts.Count(a => a.Status != AttemptStatus.SkippedCircuitOpen));
    }

    /// <summary>
    /// What an in-process caller records about a finished request (an Evaluation Case Result): the Provider Model used, the token
    /// totals and the Estimated Cost, worked out exactly as settlement does, and the latency.
    /// </summary>
    private InferenceResult WithAccounting(InferenceResult result, Routed routed, List<AttemptRecord> attempts, DateTimeOffset received)
    {
        var called = attempts.Where(a => a.Status != AttemptStatus.SkippedCircuitOpen).ToList();
        return result with
        {
            ProviderModelId = attempts.LastOrDefault(a => a.Status == AttemptStatus.Succeeded)?.ProviderModelId,
            InputTokens = SumOrNull(called.Select(a => a.InputTokens)),
            OutputTokens = SumOrNull(called.Select(a => a.OutputTokens)),
            EstimatedCost = costs.RequestEstimatedCost(Priced(routed, attempts), MaxOutputTokensOf(routed)),
            Latency = time.GetUtcNow() - received,
        };
    }

    private static int? SumOrNull(IEnumerable<int?> values)
    {
        long total = 0;
        var any = false;
        foreach (var value in values)
        {
            if (value is { } v)
            {
                total += v;
                any = true;
            }
        }

        return any ? (int)Math.Min(total, int.MaxValue) : null;
    }

    private static int MaxOutputTokensOf(Routed routed)
        => routed.Chat?.MaxOutputTokens is { } max and > 0 ? max : ICostCalculator.DefaultMaxOutputTokens;

    /// <summary>A caller bug (Request Origin invariants of data-model.md), refused before anything is recorded.</summary>
    private static void CheckOrigin(InferenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OriginViolation() is { } violation)
        {
            throw new ArgumentException(violation, nameof(request));
        }
    }

    /// <summary>
    /// Step 7, for any <see cref="InferenceTarget"/>. Reads the configuration snapshot once (the same instance serves the whole
    /// request). A pinned Policy Version that is not the snapshot's latest is read from the policy store (immutable, cached there).
    /// </summary>
    private async Task<Routed> RouteAsync(InferenceRequest request, DateTimeOffset received, bool streaming, CancellationToken cancellationToken)
    {
        var snapshot = configuration.Current;
        PolicySnapshot? pinnedPolicy = null;
        if (request.Target is InferenceTarget.PinnedPolicyVersion pinned)
        {
            pinnedPolicy = snapshot.Routing.Policies.TryGetValue(pinned.PolicyId, out var latest) && latest.Version == pinned.PolicyVersion
                ? latest
                : await policies.GetVersionAsync(pinned.PolicyId, pinned.PolicyVersion, cancellationToken);
            if (pinnedPolicy is null)
            {
                var error = new Error(ErrorCode.ModelNotFound, "The pinned Policy Version does not exist.");
                return new Routed(pinned.Alias, null, null, null, error, null, null, snapshot.Routing) { Streaming = streaming };
            }
        }

        return Route(request, snapshot, pinnedPolicy, received, streaming);
    }

    /// <summary>
    /// A pure function of the snapshot taken by <see cref="RouteAsync"/>, so a configuration change in between never mixes two Policy
    /// Versions. Routing failures keep their Decision for the record.
    /// </summary>
    private Routed Route(InferenceRequest request, ConfigurationSnapshot snapshot, PolicySnapshot? pinnedPolicy, DateTimeOffset received, bool streaming)
    {
        var requested = RequestedAlias(request, snapshot);
        using var span = GatewayTelemetry.StartSpan(GatewayTelemetry.Spans.Route);
        span?.SetTag(GatewayTelemetry.Attributes.RequestModel, requested);
        var routed = Decide(request, requested, snapshot, pinnedPolicy, received, streaming) with { Streaming = streaming };
        if (span is not null)
        {
            span.SetTag(GatewayTelemetry.Attributes.PolicyId, routed.PolicyId?.ToString());
            span.SetTag(GatewayTelemetry.Attributes.PolicyVersion, routed.PolicyVersion);
            span.SetTag(GatewayTelemetry.Attributes.Candidates, routed.Plan?.Candidates.Count ?? 0);
            span.SetTag(GatewayTelemetry.Attributes.Chosen, routed.Plan is { Candidates.Count: > 0 } chosen ? chosen.Candidates[0].ProviderModelId.ToString() : null);
            span.SetTag(GatewayTelemetry.Attributes.Result, routed.Error?.Code.Value ?? "routed");
        }

        if (routed.Error?.Code == ErrorCode.RequestCostLimitExceeded)
        {
            limits.Rejected("request_cost");
        }

        return routed;
    }

    /// <summary>
    /// The name the request is recorded (<c>requested_alias</c>) and echoed under. A pinned Provider Model has no alias, so it is
    /// <c>provider/model</c>, the notation of policy definitions, which can never collide with an alias (aliases have no <c>/</c>).
    /// </summary>
    private static string RequestedAlias(InferenceRequest request, ConfigurationSnapshot snapshot) => request.Target switch
    {
        InferenceTarget.PinnedProviderModel pinned => snapshot.Routing.Models.TryGetValue(pinned.ProviderModelId, out var model)
            ? $"{model.ProviderName}/{model.ProviderModelIdentifier}"
            : pinned.ProviderModelId.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
        InferenceTarget.PinnedPolicyVersion pinned => pinned.Alias,
        InferenceTarget.ModelAlias alias => alias.Alias,
        _ => request.Alias,
    };

    private Routed Decide(InferenceRequest request, string requested, ConfigurationSnapshot snapshot, PolicySnapshot? pinnedPolicy, DateTimeOffset received, bool streaming)
    {
        var texts = ChatInput.Texts(request.Chat);
        var input = new RoutingInput(
            request.TenantId,
            requested,
            snapshot.AccessFor(request.TenantId),
            request.TaskType,
            RequiredCapabilities(request, streaming),
            EstimateInput(snapshot, texts),
            request.Chat.MaxOutputTokens,
            snapshot.Routing,
            health.OpenCircuits())
        {
            PinnedPolicy = pinnedPolicy,
            PinnedProviderModelId = (request.Target as InferenceTarget.PinnedProviderModel)?.ProviderModelId,
        };

        var outcome = router.Decide(input);
        var decision = outcome.Decision;
        var json = decision is null ? null : RoutingDecisionJson.Write(decision);
        if (outcome.Error is not null || decision is null)
        {
            return new Routed(requested, decision?.PolicyId, decision?.PolicyVersion, json, outcome.Error! with { RetryAfter = outcome.RetryAfter }, null, null, snapshot.Routing);
        }

        var settings = decision.Execution;
        var deadline = received + TimeSpan.FromMilliseconds(settings.DeadlineMs);
        if (request.Timeout is { } timeout && received + timeout < deadline)
        {
            deadline = received + timeout;
        }

        // The Reservation and the Routing Decision were costed at this ceiling, so the Provider is held to it.
        var chat = request.Chat with { MaxOutputTokens = decision.EffectiveMaxOutputTokens };
        return new Routed(requested, decision.PolicyId, decision.PolicyVersion, json, null, new ExecutionPlan(decision.Ordered, settings, deadline), chat, snapshot.Routing);
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

        using var span = GatewayTelemetry.StartSpan(GatewayTelemetry.Spans.Reserve);
        var opened = await ledger.OpenAsync(
            new OpenInferenceRequest(
                request.RequestId,
                request.TenantId,
                request.Origin,
                request.ApiKeyId,
                request.EvaluationRunId,
                request.ClientRequestId,
                routed.RequestedAlias,
                request.TaskType,
                streaming,
                request.InputBytes,
                Activity.Current?.TraceId.ToString() ?? string.Empty,
                new ReservationRequest(hold, expiresAt)),
            cancellationToken);
        span?.SetTag(GatewayTelemetry.Attributes.Result, opened.IsSuccess ? "reserved" : opened.Error.Code.Value);
        if (opened.IsFailure)
        {
            // A refused Reservation is still a finished request, recorded by the ledger; it is counted here because it never settles.
            limits.Rejected("budget");
            Completed(request, routed, "failed", opened.Error.Code.Value, received, ttftMs: null, attempts: []);
            return opened.Error;
        }

        if (routed.Plan is { } routedPlan)
        {
            routed = routed with { Plan = routedPlan with { Budget = new AttemptBudget(ledger, request.RequestId, hold) } };
        }

        return (opened.Value, routed);
    }

    private static List<AttemptRecord> Priced(Routed routed, List<AttemptRecord> attempts)
        => [.. attempts.Select(a => a with { Pricing = PricingOf(routed, a.ProviderModelId) })];

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
            Priced(routed, attempts),
            routed.Chat?.MaxOutputTokens);

        Completed(request, routed, StatusName(outcome), errorCode, opened.StartedAt, ttftMs, attempts);

        using var span = GatewayTelemetry.StartSpan(GatewayTelemetry.Spans.Settle);
        using var timeout = new CancellationTokenSource(SettleTimeout, time);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await ledger.SettleAsync(settlement, timeout.Token);
                span?.SetTag(GatewayTelemetry.Attributes.Result, "settled");
                return;
            }
#pragma warning disable CA1031 // Any storage fault is retried, then reported; settlement must never fail the response.
            catch (Exception exception) when (!timeout.IsCancellationRequested)
#pragma warning restore CA1031
            {
                LogSettleFailed(logger, exception, request.RequestId, attempt + 1);
                if (attempt >= _settleBackoff.Length)
                {
                    GaveUp(span, request.RequestId);
                    return;
                }

                try
                {
                    await Task.Delay(_settleBackoff[attempt], time, timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    GaveUp(span, request.RequestId);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                GaveUp(span, request.RequestId);
                return;
            }
        }
    }

    private void GaveUp(Activity? span, Guid requestId)
    {
        span?.SetTag(GatewayTelemetry.Attributes.Result, "gave_up");
        span?.SetStatus(ActivityStatusCode.Error);
        metrics.SettlementFailed();
        LogSettleGaveUp(logger, requestId);
    }

    /// <summary>The metrics and the one Information line of a finished request (observability.md sections 3 and 4). Metadata only.</summary>
    private void Completed(InferenceRequest request, Routed routed, string status, string? errorCode, DateTimeOffset startedAt, int? ttftMs, IReadOnlyList<AttemptRecord> attempts)
    {
        var telemetry = new RequestTelemetry(
            request.RequestId,
            routed.RequestedAlias,
            routed.Streaming,
            status,
            errorCode,
            attempts.LastOrDefault(a => a.Status == AttemptStatus.Succeeded)?.ProviderModelId,
            time.GetUtcNow() - startedAt,
            ttftMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
            attempts);
        metrics.Completed(telemetry);
        LogCompleted(logger, request.RequestId, status, routed.RequestedAlias, telemetry.SelectedProviderModelId, telemetry.CalledAttempts, (long)telemetry.Duration.TotalMilliseconds, errorCode);
    }

    private static string StatusName(RequestOutcome outcome) => outcome switch
    {
        RequestOutcome.Succeeded => "succeeded",
        RequestOutcome.Failed => "failed",
        _ => "cancelled",
    };

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Request {RequestId} {Status}: alias {Alias}, Provider Model {ProviderModelId}, {Attempts} attempt(s), {DurationMs} ms, error {ErrorCode}.")]
    private static partial void LogCompleted(ILogger logger, Guid requestId, string status, string alias, Guid? providerModelId, int attempts, long durationMs, string? errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settling request {RequestId} failed (try {Try}); retrying.")]
    private static partial void LogSettleFailed(ILogger logger, Exception exception, Guid requestId, int @try);

    [LoggerMessage(Level = LogLevel.Error, Message = "Settling request {RequestId} timed out; it stays 'running' until the sweeper recovers it.")]
    private static partial void LogSettleGaveUp(ILogger logger, Guid requestId);
}
