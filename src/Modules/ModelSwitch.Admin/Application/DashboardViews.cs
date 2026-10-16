#pragma warning disable CS1573 // Record parameters are documented only where they need explanation.

using System.Text.Json.Nodes;

namespace ModelSwitch.Admin.Application;

/// <summary>A Provider Model as the dashboard names it. Names are null when the model is no longer in the catalog.</summary>
/// <param name="Name">The Provider's own model identifier (e.g. <c>gpt-4o-mini</c>).</param>
internal sealed record ModelRef(Guid Id, Guid? ProviderId, string? ProviderName, string? Name);

/// <summary>One row of the Request Explorer. Metadata only (ADR-0010); Estimated Cost is always labelled as such by the UI.</summary>
internal sealed record RequestSummaryView(
    Guid Id,
    Guid TenantId,
    string Origin,
    string RequestedAlias,
    string? TaskType,
    string Status,
    string? ErrorCode,
    bool Streaming,
    int AttemptCount,
    bool HadFallback,
    ModelRef? SelectedModel,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int? DurationMs,
    int? TtftMs,
    int? TotalInputTokens,
    int? TotalOutputTokens,
    decimal? TotalEstimatedCost,
    string UsageStatus,
    IReadOnlyList<AttemptTimelineView> Attempts);

internal sealed record RequestPage(IReadOnlyList<RequestSummaryView> Items, string? NextCursor);

internal sealed record AttemptTimelineView(
    Guid Id,
    int AttemptNumber,
    ModelRef Model,
    string Kind,
    string Status,
    string? ErrorClass,
    string? ErrorCode,
    int? HttpStatus,
    string? ProviderErrorType,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int DurationMs,
    int? TtftMs,
    int? InputTokens,
    int? OutputTokens,
    decimal? EstimatedCost);

internal sealed record UsageRecordDetailView(
    Guid Id,
    Guid? ProviderAttemptId,
    ModelRef? Model,
    Guid? ModelPriceId,
    int? InputTokens,
    int? CachedInputTokens,
    int? OutputTokens,
    string UsageSource,
    string? EstimateMethod,
    decimal InputCost,
    decimal OutputCost,
    decimal TotalCost,
    string Currency,
    DateTimeOffset RecordedAt);

/// <summary>Everything recorded about one Inference Request (GET /admin/requests/{id}).</summary>
/// <param name="TraceUrl">A link to the trace when <c>Telemetry:TraceUrlTemplate</c> is set and the request has a trace id.</param>
internal sealed record RequestDetailView(
    RequestSummaryView Request,
    string? ClientRequestId,
    Guid? ApiKeyId,
    Guid? EvaluationRunId,
    Guid? PolicyId,
    int? PolicyVersion,
    int InputBytes,
    string TraceId,
    string? TraceUrl,
    JsonNode? RoutingDecision,
    IReadOnlyList<AttemptTimelineView> Attempts,
    IReadOnlyList<UsageRecordDetailView> UsageRecords);

/// <summary>One row of a usage report. Grouping columns that were not asked for are null.</summary>
internal sealed record UsageRowView(
    DateTimeOffset Period,
    Guid? TenantId,
    Guid? ProviderId,
    string? ProviderName,
    ModelRef? Model,
    string? Alias,
    long Requests,
    long FailedRequests,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    decimal EstimatedCost,
    string Currency);

internal sealed record UsageReportView(DateTimeOffset From, DateTimeOffset To, string Interval, IReadOnlyList<string> GroupBy, IReadOnlyList<UsageRowView> Items);

/// <summary>Figures for the Overview tiles or one series point. Latencies in ms.</summary>
/// <param name="Exact">False when percentiles are estimated from the rollup's buckets (ranges over 24 h).</param>
internal sealed record OverviewFiguresView(
    long Requests,
    long FailedRequests,
    long FallbackRequests,
    double? SuccessRate,
    double? FallbackRate,
    long InputTokens,
    long OutputTokens,
    decimal EstimatedCost,
    int? LatencyP50Ms,
    int? LatencyP95Ms,
    int? TtftP50Ms,
    int? TtftP95Ms,
    bool Exact);

internal sealed record OverviewPointView(DateTimeOffset Start, OverviewFiguresView Figures);

internal sealed record OverviewView(
    DateTimeOffset From,
    DateTimeOffset To,
    int StepSeconds,
    string Currency,
    OverviewFiguresView Totals,
    IReadOnlyList<OverviewPointView> Series,
    IReadOnlyList<RequestSummaryView> RecentFailures);

/// <summary>One api instance's circuit for a Provider Model.</summary>
/// <param name="Stale">The instance has not written for longer than <see cref="ProviderHealthRules.Freshness"/>: it may be gone.</param>
internal sealed record InstanceHealthView(string InstanceId, string State, double WindowFailureRate, int WindowCalls, DateTimeOffset CapturedAt, bool Stale);

/// <summary>Recent Attempts on the model across all instances (the last <see cref="ProviderHealthRules.RecentWindow"/>).</summary>
internal sealed record RecentAttemptsView(long Attempts, long FailedAttempts, double? ErrorRate, int? LatencyP50Ms, int? LatencyP95Ms);

/// <summary>Provider Health for one Provider Model: the aggregate state, each instance's view and recent outcomes.</summary>
/// <param name="State"><c>closed</c>, <c>half_open</c>, <c>open</c>, or <c>unknown</c> when no instance has reported recently.</param>
internal sealed record ProviderModelHealthView(ModelRef Model, bool Enabled, string State, IReadOnlyList<InstanceHealthView> Instances, RecentAttemptsView Recent);

internal sealed record ProviderHealthView(DateTimeOffset At, IReadOnlyList<ProviderModelHealthView> Items);
