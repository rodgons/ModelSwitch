using Microsoft.Extensions.Configuration;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Admin.Application;

/// <summary>
/// The dashboard's read use cases (api.md section 3, "Requests and usage"). Each one validates its query, narrows it to the caller's
/// Tenant Grant (a Tenant outside it is 404, never 403), reads through the owning modules' query contracts, and names the Provider
/// Models from the catalog. It never reads a <c>DbContext</c>.
/// </summary>
internal sealed class DashboardService(
    ICurrentAdministrator current,
    ITenantGrantChecker grants,
    IUsageQueries usage,
    ICatalogSnapshotSource catalog,
    IProviderHealthSnapshots health,
    IConfiguration configuration,
    TimeProvider time)
{
    public const string TraceUrlTemplateSetting = "Telemetry:TraceUrlTemplate";

    private static readonly Error _notFound = new(ErrorCode.NotFound, "The resource was not found.");
    private static readonly Error _signedOut = new(ErrorCode.Unauthenticated, "Sign in to continue.");

    public async Task<Result<RequestPage>> ListRequestsAsync(DashboardQueryString query, CancellationToken cancellationToken)
    {
        var parsed = DashboardQueryParser.Requests(query, time.GetUtcNow());
        if (parsed.IsFailure)
        {
            return parsed.Error;
        }

        var scoped = Scope(parsed.Value.Filter);
        if (scoped.IsFailure)
        {
            return scoped.Error;
        }

        var page = await usage.ListRequestsAsync(parsed.Value with { Filter = scoped.Value }, cancellationToken);
        return new RequestPage(await SummariesAsync(page.Items, cancellationToken), page.NextCursor);
    }

    public async Task<Result<RequestDetailView>> GetRequestAsync(Guid requestId, CancellationToken cancellationToken)
    {
        if (current.Current is not { } administrator)
        {
            return _signedOut;
        }

        var detail = await usage.GetRequestAsync(requestId, TenantIds(administrator), cancellationToken);
        if (detail is null)
        {
            return _notFound;
        }

        var names = await NamesAsync(cancellationToken);
        var timeline = detail.Attempts.Select(a => Timeline(a, names)).ToList();
        return new RequestDetailView(
            Summary(detail.Request, names, timeline),
            detail.ClientRequestId,
            detail.ApiKeyId,
            detail.EvaluationRunId,
            detail.PolicyId,
            detail.PolicyVersion,
            detail.InputBytes,
            detail.TraceId,
            TraceUrl(detail.TraceId),
            RoutingDecisionView.From(detail.RoutingDecision),
            timeline,
            [.. detail.UsageRecords.Select(u => new UsageRecordDetailView(
                u.Id,
                u.ProviderAttemptId,
                u.ProviderModelId is { } model ? Name(model, names) : null,
                u.ModelPriceId,
                u.InputTokens,
                u.CachedInputTokens,
                u.OutputTokens,
                u.UsageSource,
                u.EstimateMethod,
                u.InputCost,
                u.OutputCost,
                u.TotalCost,
                u.Currency,
                u.RecordedAt))]);
    }

    public async Task<Result<UsageReportView>> UsageAsync(DashboardQueryString query, CancellationToken cancellationToken)
    {
        var parsed = DashboardQueryParser.Usage(query, time.GetUtcNow());
        if (parsed.IsFailure)
        {
            return parsed.Error;
        }

        var scoped = Scope(parsed.Value.Query.Filter);
        if (scoped.IsFailure)
        {
            return scoped.Error;
        }

        var report = parsed.Value.Query with { Filter = scoped.Value };
        var rows = await usage.UsageAsync(report, cancellationToken);
        var names = await NamesAsync(cancellationToken);
        var views = FoldProviders(rows, names, parsed.Value.GroupByProvider, parsed.Value.GroupByModel);

        var groups = new List<string>();
        if (report.GroupBy.HasFlag(UsageGrouping.Tenant))
        {
            groups.Add("tenant");
        }

        if (parsed.Value.GroupByProvider)
        {
            groups.Add("provider");
        }

        if (parsed.Value.GroupByModel)
        {
            groups.Add("model");
        }

        if (report.GroupBy.HasFlag(UsageGrouping.Alias))
        {
            groups.Add("alias");
        }

        var interval = report.Interval switch
        {
            UsageInterval.Hour => "hour",
            UsageInterval.Day => "day",
            _ => "month",
        };
        return new UsageReportView(report.From, report.To, interval, groups, views);
    }

    public async Task<Result<OverviewView>> OverviewAsync(DashboardQueryString query, CancellationToken cancellationToken)
    {
        var parsed = DashboardQueryParser.Overview(query, time.GetUtcNow());
        if (parsed.IsFailure)
        {
            return parsed.Error;
        }

        var scoped = Scope(new RequestFilter(null, TenantId: parsed.Value.TenantId));
        if (scoped.IsFailure)
        {
            return scoped.Error;
        }

        var report = await usage.OverviewAsync(parsed.Value with { TenantIds = scoped.Value.TenantIds }, cancellationToken);
        return new OverviewView(
            report.From,
            report.To,
            (int)report.Step.TotalSeconds,
            ReportCurrency.Code,
            Figures(report.Totals),
            [.. report.Series.Select(p => new OverviewPointView(p.Start, Figures(p.Figures)))],
            await SummariesAsync(report.RecentFailures, cancellationToken));
    }

    public async Task<ProviderHealthView> ProviderHealthAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var entries = (await catalog.LoadAsync(cancellationToken)).Entries;
        var snapshots = (await health.ListAsync(cancellationToken)).ToLookup(s => s.ProviderModelId);
        var recent = (await usage.AttemptStatsAsync(now - ProviderHealthRules.RecentWindow, cancellationToken)).ToDictionary(s => s.ProviderModelId);

        return new ProviderHealthView(now, [.. entries
            .OrderBy(e => e.ProviderName, StringComparer.Ordinal)
            .ThenBy(e => e.ProviderModelIdentifier, StringComparer.Ordinal)
            .Select(e =>
            {
                var instances = snapshots[e.ProviderModelId].ToList();
                var stats = recent.TryGetValue(e.ProviderModelId, out var s) ? s : new AttemptStats(e.ProviderModelId, 0, 0, null, null);
                return new ProviderModelHealthView(
                    new ModelRef(e.ProviderModelId, e.ProviderId, e.ProviderName, e.ProviderModelIdentifier),
                    e.ProviderEnabled && e.ModelEnabled,
                    ProviderHealthRules.Aggregate(instances.Select(i => (i.CircuitState, i.CapturedAt)), now),
                    [.. instances.Select(i => new InstanceHealthView(
                        i.InstanceId,
                        i.CircuitState,
                        i.WindowFailureRate,
                        i.WindowCalls,
                        i.CapturedAt,
                        ProviderHealthRules.IsStale(i.CapturedAt, now)))],
                    new RecentAttemptsView(
                        stats.Attempts,
                        stats.FailedAttempts,
                        ProviderHealthRules.Rate(stats.FailedAttempts, stats.Attempts),
                        stats.LatencyP50Ms,
                        stats.LatencyP95Ms));
            })]);
    }

    /// <summary>The usage rows with names, and Provider Models folded into their Provider when the report groups by Provider only.</summary>
    internal static List<UsageRowView> FoldProviders(IReadOnlyList<UsageRow> rows, IReadOnlyDictionary<Guid, ModelRef> names, bool byProvider, bool byModel)
    {
        var views = new Dictionary<(DateTimeOffset, Guid?, Guid?, Guid?, string?), UsageRowView>();
        foreach (var row in rows)
        {
            var model = row.ProviderModelId is { } id ? Name(id, names) : null;
            var providerId = byProvider ? model?.ProviderId : null;
            var view = new UsageRowView(
                row.Period,
                row.TenantId,
                providerId,
                byProvider ? model?.ProviderName : null,
                byModel ? model : null,
                row.Alias,
                row.Requests,
                row.FailedRequests,
                row.InputTokens,
                row.CachedInputTokens,
                row.OutputTokens,
                row.EstimatedCost,
                ReportCurrency.Code);
            var key = (row.Period, row.TenantId, providerId, byModel ? row.ProviderModelId : null, row.Alias);
            views[key] = views.TryGetValue(key, out var existing)
                ? existing with
                {
                    Requests = existing.Requests + view.Requests,
                    FailedRequests = existing.FailedRequests + view.FailedRequests,
                    InputTokens = existing.InputTokens + view.InputTokens,
                    CachedInputTokens = existing.CachedInputTokens + view.CachedInputTokens,
                    OutputTokens = existing.OutputTokens + view.OutputTokens,
                    EstimatedCost = existing.EstimatedCost + view.EstimatedCost,
                }
                : view;
        }

        return [.. views.Values];
    }

    internal static OverviewFiguresView Figures(OverviewFigures figures) => new(
        figures.Requests,
        figures.FailedRequests,
        figures.FallbackRequests,
        figures.Requests == 0 ? null : 1 - ProviderHealthRules.Rate(figures.FailedRequests, figures.Requests),
        ProviderHealthRules.Rate(figures.FallbackRequests, figures.Requests),
        figures.InputTokens,
        figures.OutputTokens,
        figures.EstimatedCost,
        figures.LatencyP50Ms,
        figures.LatencyP95Ms,
        figures.TtftP50Ms,
        figures.TtftP95Ms,
        figures.Exact);

    private static IReadOnlyCollection<Guid>? TenantIds(AdministratorPrincipal administrator)
        => administrator.Grant.IsAll ? null : administrator.Grant.TenantIds;

    // The grant becomes the query's Tenant set; an explicit tenantId outside it is a 404, as for any other foreign resource.
    private Result<RequestFilter> Scope(RequestFilter filter)
    {
        if (current.Current is not { } administrator)
        {
            return _signedOut;
        }

        if (filter.TenantId is { } tenantId && grants.CheckAccess(administrator, tenantId) is { IsFailure: true } denied)
        {
            return denied.Error;
        }

        return filter with { TenantIds = TenantIds(administrator) };
    }

    private string? TraceUrl(string traceId)
        => configuration[TraceUrlTemplateSetting] is { Length: > 0 } template && traceId.Length > 0
            ? template.Replace("{traceId}", Uri.EscapeDataString(traceId), StringComparison.Ordinal)
            : null;

    private async Task<IReadOnlyDictionary<Guid, ModelRef>> NamesAsync(CancellationToken cancellationToken)
        => (await catalog.LoadAsync(cancellationToken)).Entries.ToDictionary(
            e => e.ProviderModelId,
            e => new ModelRef(e.ProviderModelId, e.ProviderId, e.ProviderName, e.ProviderModelIdentifier));

    private static ModelRef Name(Guid id, IReadOnlyDictionary<Guid, ModelRef> names)
        => names.TryGetValue(id, out var name) ? name : new ModelRef(id, null, null, null);

    // Each row carries its Attempts so the list can draw a mini Route Strip: one query for the whole page.
    private async Task<List<RequestSummaryView>> SummariesAsync(IReadOnlyList<RequestListItem> items, CancellationToken cancellationToken)
    {
        var names = await NamesAsync(cancellationToken);
        var attempts = await usage.AttemptsForAsync([.. items.Select(i => i.Id)], cancellationToken);
        return [.. items.Select(r => Summary(r, names, [.. (attempts.GetValueOrDefault(r.Id) ?? []).Select(a => Timeline(a, names))]))];
    }

    private static AttemptTimelineView Timeline(AttemptView a, IReadOnlyDictionary<Guid, ModelRef> names) => new(
        a.Id,
        a.AttemptNumber,
        Name(a.ProviderModelId, names) with { ProviderId = a.ProviderId },
        a.Kind,
        a.Status,
        a.ErrorClass,
        a.ErrorCode,
        a.HttpStatus,
        a.ProviderErrorType,
        a.StartedAt,
        a.CompletedAt,
        a.DurationMs,
        a.TtftMs,
        a.InputTokens,
        a.OutputTokens,
        a.EstimatedCost);

    private static RequestSummaryView Summary(RequestListItem r, IReadOnlyDictionary<Guid, ModelRef> names, IReadOnlyList<AttemptTimelineView> attempts) => new(
        r.Id,
        r.TenantId,
        r.Origin,
        r.RequestedAlias,
        r.TaskType,
        r.Status,
        r.ErrorCode,
        r.Streaming,
        r.AttemptCount,
        r.HadFallback,
        r.SelectedProviderModelId is { } model ? Name(model, names) : null,
        r.StartedAt,
        r.CompletedAt,
        r.DurationMs,
        r.TtftMs,
        r.TotalInputTokens,
        r.TotalOutputTokens,
        r.TotalEstimatedCost,
        r.UsageStatus,
        attempts);
}
