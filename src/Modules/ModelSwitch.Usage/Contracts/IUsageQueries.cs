#pragma warning disable CS1573 // Record parameters are documented only where they need explanation.

using System.Text.Json.Nodes;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Pagination;

namespace ModelSwitch.Usage.Contracts;

/// <summary>
/// The filters of the admin request and usage queries (api.md section 3, "Requests and usage"). Every field is optional.
/// <paramref name="TenantIds"/> is the caller's Tenant Grant: null reads every Tenant, an empty list reads none.
/// </summary>
/// <param name="ProviderId">Requests with at least one Attempt on this Provider.</param>
/// <param name="ProviderModelId">Requests with at least one Attempt on this Provider Model.</param>
public sealed record RequestFilter(
    IReadOnlyCollection<Guid>? TenantIds,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    Guid? TenantId = null,
    RequestOrigin? Origin = null,
    string? Alias = null,
    Guid? ProviderId = null,
    Guid? ProviderModelId = null,
    string? Status = null,
    string? ErrorCode = null,
    bool? HadFallback = null);

public enum RequestSort
{
    StartedAtDescending,
    StartedAtAscending,
    DurationDescending,
    DurationAscending,
}

/// <summary>A page of requests. <paramref name="Page"/> carries the decoded cursor from <see cref="Pagination.Parse"/>.</summary>
public sealed record RequestListQuery(RequestFilter Filter, RequestSort Sort, PageRequest Page);

/// <summary>One Inference Request as the Request Explorer lists it. Metadata only (ADR-0010).</summary>
public sealed record RequestListItem(
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
    Guid? SelectedProviderModelId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int? DurationMs,
    int? TtftMs,
    int? TotalInputTokens,
    int? TotalOutputTokens,
    decimal? TotalEstimatedCost,
    string UsageStatus);

/// <summary>One Attempt in a request's timeline.</summary>
public sealed record AttemptView(
    Guid Id,
    int AttemptNumber,
    Guid ProviderId,
    Guid ProviderModelId,
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

/// <summary>One Usage Record, with its provenance (Usage Source, estimate method, the Price it was costed with).</summary>
public sealed record UsageRecordView(
    Guid Id,
    Guid? ProviderAttemptId,
    Guid? ProviderModelId,
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

/// <summary>Everything recorded about one Inference Request.</summary>
/// <param name="RoutingDecision">The stored Routing Decision document, or null when routing never ran.</param>
public sealed record RequestDetail(
    RequestListItem Request,
    string? ClientRequestId,
    Guid? ApiKeyId,
    Guid? EvaluationRunId,
    Guid? PolicyId,
    int? PolicyVersion,
    int InputBytes,
    string TraceId,
    JsonNode? RoutingDecision,
    IReadOnlyList<AttemptView> Attempts,
    IReadOnlyList<UsageRecordView> UsageRecords);

[Flags]
public enum UsageGrouping
{
    None = 0,
    Tenant = 1,
    ProviderModel = 2,
    Alias = 4,
}

public enum UsageInterval
{
    Hour,
    Day,
    Month,
}

/// <summary>A usage report: totals per period and per requested grouping.</summary>
public sealed record UsageQuery(RequestFilter Filter, DateTimeOffset From, DateTimeOffset To, UsageGrouping GroupBy, UsageInterval Interval);

/// <summary>One row of a usage report. Grouping columns that were not asked for are null.</summary>
public sealed record UsageRow(
    DateTimeOffset Period,
    Guid? TenantId,
    Guid? ProviderModelId,
    string? Alias,
    long Requests,
    long FailedRequests,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    decimal EstimatedCost);

/// <summary>The Overview: tiles for the whole range and a time series.</summary>
/// <param name="Origin">Only requests of this Request Origin; null reads both.</param>
public sealed record OverviewQuery(IReadOnlyCollection<Guid>? TenantIds, DateTimeOffset From, DateTimeOffset To, Guid? TenantId = null, RequestOrigin? Origin = null);

/// <summary>Aggregates over a range or a bucket. Latencies in ms; null when no request had one.</summary>
/// <param name="Exact">True when the percentiles are exact; false when estimated from the rollup's buckets (ranges over 24 h).</param>
/// <param name="FailedRequests">Failed and abandoned requests; a cancelled request is neither a success nor a failure.</param>
public sealed record OverviewFigures(
    long Requests,
    long FailedRequests,
    long FallbackRequests,
    long InputTokens,
    long OutputTokens,
    decimal EstimatedCost,
    int? LatencyP50Ms,
    int? LatencyP95Ms,
    int? TtftP50Ms,
    int? TtftP95Ms,
    bool Exact);

public sealed record OverviewPoint(DateTimeOffset Start, OverviewFigures Figures);

public sealed record OverviewReport(DateTimeOffset From, DateTimeOffset To, TimeSpan Step, OverviewFigures Totals, IReadOnlyList<OverviewPoint> Series, IReadOnlyList<RequestListItem> RecentFailures);

/// <summary>Recent Attempt outcomes and latency of one Provider Model, across all instances.</summary>
public sealed record AttemptStats(Guid ProviderModelId, long Attempts, long FailedAttempts, int? LatencyP50Ms, int? LatencyP95Ms);

/// <summary>Read side of the <c>usage</c> schema for the admin dashboard. Never returns content (ADR-0010).</summary>
public interface IUsageQueries
{
    Task<PagedResult<RequestListItem>> ListRequestsAsync(RequestListQuery query, CancellationToken cancellationToken);

    /// <summary>Null when the request does not exist or its Tenant is outside <paramref name="tenantIds"/> (null: all Tenants).</summary>
    Task<RequestDetail?> GetRequestAsync(Guid requestId, IReadOnlyCollection<Guid>? tenantIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<UsageRow>> UsageAsync(UsageQuery query, CancellationToken cancellationToken);

    Task<OverviewReport> OverviewAsync(OverviewQuery query, CancellationToken cancellationToken);

    /// <summary>The Attempts of each given request, in order: one query for a whole page of requests (the Route Strip of every row).</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<AttemptView>>> AttemptsForAsync(IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken);

    /// <summary>Per Provider Model, the Attempts that started at or after <paramref name="since"/>. Not Tenant-scoped: Provider Health is global.</summary>
    Task<IReadOnlyList<AttemptStats>> AttemptStatsAsync(DateTimeOffset since, CancellationToken cancellationToken);
}

/// <summary>The query-string form of <see cref="RequestOrigin"/>.</summary>
public static class RequestOrigins
{
    public static string ToWire(RequestOrigin origin) => origin == RequestOrigin.Api ? "api" : "evaluation";

    public static bool TryParse(string? value, out RequestOrigin origin)
    {
        origin = value == "evaluation" ? RequestOrigin.Evaluation : RequestOrigin.Api;
        return value is "api" or "evaluation";
    }
}

/// <summary>Money totals in reports are USD (the MVP's only currency).</summary>
public static class ReportCurrency
{
    public const string Code = Money.Usd;
}
