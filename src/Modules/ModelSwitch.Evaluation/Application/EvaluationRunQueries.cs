using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ModelSwitch.Evaluation.Domain.Aggregates;
using ModelSwitch.Evaluation.Domain.Evaluators;
using ModelSwitch.Evaluation.Domain.Runs;
using ModelSwitch.Evaluation.Infrastructure;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Pagination;

namespace ModelSwitch.Evaluation.Application;

/// <summary>
/// The read side of Evaluation Runs (api.md section 3, Evaluation): Comparisons with their runs, one run's detail, its Case Results,
/// and the side-by-side Comparison. Every read is narrowed to the caller's Tenant Grant (404 outside it).
/// </summary>
internal sealed class EvaluationRunQueries(IDbContextFactory<EvaluationDbContext> contexts, EvaluationAccess access, EvaluationRunService runs)
{
    private static readonly Error _comparisonNotFound = new(ErrorCode.NotFound, "The comparison was not found.");

    /// <summary>Comparisons newest first, each with its runs. Filters: <c>tenantId</c>, <c>datasetId</c>, <c>status</c> (a Comparison with at least one run in it).</summary>
    public async Task<Result<PagedResult<ComparisonView>>> ListAsync(string? tenantId, string? datasetId, string? status, string? limit, string? cursor, CancellationToken cancellationToken)
    {
        var query = access.ListQuery(tenantId, limit, cursor);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (query.IsFailure)
        {
            if (query.Error.Code != ErrorCode.ValidationFailed)
            {
                return query.Error;
            }

            foreach (var (key, messages) in query.Error.FieldErrors!)
            {
                errors[key] = messages;
            }
        }

        Guid? dataset = null;
        if (datasetId is not null)
        {
            if (Guid.TryParse(datasetId, CultureInfo.InvariantCulture, out var parsed))
            {
                dataset = parsed;
            }
            else
            {
                errors["datasetId"] = ["The datasetId must be a UUID."];
            }
        }

        if (status is not null && !EvaluationRunStatuses.All.Contains(status, StringComparer.Ordinal))
        {
            errors["status"] = [$"The status must be one of: {string.Join(", ", EvaluationRunStatuses.All)}."];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The list query is invalid.", errors);
        }

        var (filter, page, before) = query.Value;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var comparisons = db.Comparisons.AsNoTracking();
        if (filter is { } granted)
        {
            comparisons = comparisons.Where(c => granted.Contains(c.TenantId));
        }

        if (dataset is { } d)
        {
            comparisons = comparisons.Where(c => c.DatasetId == d);
        }

        if (status is not null)
        {
            comparisons = comparisons.Where(c => db.Runs.Any(r => r.ComparisonId == c.Id && r.Status == status));
        }

        if (before is { } last)
        {
            comparisons = comparisons.Where(c => c.Id.CompareTo(last) < 0);
        }

        var rows = await comparisons.OrderByDescending(c => c.Id).Take(page.Limit + 1).ToListAsync(cancellationToken);
        var ids = rows.Select(c => c.Id).ToArray();
        var runRows = await db.Runs.AsNoTracking().Where(r => ids.Contains(r.ComparisonId)).OrderBy(r => r.Id).ToListAsync(cancellationToken);
        var byComparison = runRows.ToLookup(r => r.ComparisonId);
        var views = rows.Select(c => new ComparisonView(
                c.Id,
                c.TenantId,
                c.DatasetId,
                c.DatasetVersion,
                c.Name,
                c.CreatedAt,
                c.CreatedByAdminId,
                [.. byComparison[c.Id].Select(Summary)]))
            .ToList();
        return Pagination.Page(views, page.Limit, v => v.Id.ToString("D", CultureInfo.InvariantCulture));
    }

    public async Task<Result<RunDetailView>> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var found = await runs.FindRunAsync(db, runId, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var run = found.Value;
        var configuration = RunConfiguration.Parse(run.RunConfigurationJson);
        var comparisonName = await db.Comparisons.AsNoTracking().Where(c => c.Id == run.ComparisonId).Select(c => c.Name).FirstAsync(cancellationToken);
        var (aggregates, live) = await AggregatesAsync(db, run, configuration, cancellationToken);
        var counts = await db.CaseResults.AsNoTracking()
            .Where(r => r.RunId == runId)
            .GroupBy(r => new { r.Status, r.ErrorCode })
            .Select(g => new { g.Key.Status, g.Key.ErrorCode, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var byStatus = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var byError = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var count in counts)
        {
            byStatus[count.Status] = byStatus.GetValueOrDefault(count.Status) + count.Count;
            if (count.ErrorCode is { } code)
            {
                byError[code] = byError.GetValueOrDefault(code) + count.Count;
            }
        }

        return new RunDetailView(
            run.Id,
            run.ComparisonId,
            comparisonName,
            run.TenantId,
            run.Status,
            new ProgressView(run.CasesTotal, run.CasesDone, run.CasesFailed),
            JsonNode.Parse(run.RunConfigurationJson),
            run.ConfigSha256,
            run.MaxEstimatedCost,
            new ReproducibilityView(
                configuration.Dataset.Id,
                configuration.Dataset.Version,
                configuration.Dataset.ContentSha256,
                new PromptRefView(configuration.Prompt.Id, configuration.Prompt.Version),
                TargetView.From(configuration.Target),
                configuration.Params,
                configuration.Evaluator,
                run.ConfigSha256,
                configuration.GatewayVersion,
                run.StartedAt,
                run.CompletedAt),
            aggregates is { } json ? JsonNode.Parse(json.GetRawText()) : null,
            live,
            new FailureSummaryView(byStatus, byError),
            run.Error,
            run.CreatedAt,
            run.StartedAt,
            run.CompletedAt);
    }

    /// <summary>
    /// Case Results in upload order, paginated (cursor: the Case ordinal), optionally of one status and of one Case by its external
    /// <c>id</c> (exact match; the Case drill-down reads one Case across a Comparison's runs with one request per run).
    /// </summary>
    public async Task<Result<PagedResult<CaseResultView>>> ResultsAsync(Guid runId, string? status, string? externalId, string? limit, string? cursor, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var found = await runs.FindRunAsync(db, runId, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (status is not null && !CaseResultStatuses.All.Contains(status, StringComparer.Ordinal))
        {
            errors["status"] = [$"The status must be one of: {string.Join(", ", CaseResultStatuses.All)}."];
        }

        var page = Pagination.Parse(limit, cursor);
        int? afterOrdinal = null;
        if (page.IsFailure)
        {
            foreach (var (key, messages) in page.Error.FieldErrors!)
            {
                errors[key] = messages;
            }
        }
        else if (page.Value.After is { } after)
        {
            if (int.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                afterOrdinal = parsed;
            }
            else
            {
                errors["cursor"] = ["The cursor is not valid."];
            }
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The results query is invalid.", errors);
        }

        var query = from result in db.CaseResults.AsNoTracking()
                    join @case in db.Cases.AsNoTracking() on result.CaseId equals @case.Id
                    where result.RunId == runId
                    select new { Result = result, @case.ExternalId, @case.Ordinal };
        if (status is not null)
        {
            query = query.Where(r => r.Result.Status == status);
        }

        if (externalId is not null)
        {
            query = query.Where(r => r.ExternalId == externalId);
        }

        if (afterOrdinal is { } last)
        {
            query = query.Where(r => r.Ordinal > last);
        }

        var rows = await query.OrderBy(r => r.Ordinal).Take(page.Value.Limit + 1).ToListAsync(cancellationToken);
        var views = rows.Select(r => ToView(r.Result, r.ExternalId, r.Ordinal)).ToList();
        return Pagination.Page(views, page.Value.Limit, v => v.Ordinal.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The side-by-side view (evaluation.md sections 3 and 6): per run its configuration, quality, errored rate, latency, candidate cost
    /// per Case and in total, judge cost apart, status, and run-to-run variance when the Tenant has run the same configuration more
    /// than once (finished runs with stored aggregates).
    /// </summary>
    public async Task<Result<ComparisonDetailView>> ComparisonAsync(Guid comparisonId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var comparison = await db.Comparisons.AsNoTracking().FirstOrDefaultAsync(c => c.Id == comparisonId, cancellationToken);
        if (comparison is null || access.CheckTenant(comparison.TenantId).IsFailure)
        {
            return _comparisonNotFound;
        }

        var members = await db.Runs.AsNoTracking().Where(r => r.ComparisonId == comparisonId).OrderBy(r => r.Id).ToListAsync(cancellationToken);
        var hashes = members.Select(r => r.ConfigSha256).Distinct().ToArray();
        var siblings = await db.Runs.AsNoTracking()
            .Where(r => r.TenantId == comparison.TenantId && hashes.Contains(r.ConfigSha256) && r.Status == EvaluationRunStatuses.Succeeded && r.AggregatesJson != null)
            .Select(r => new { r.ConfigSha256, r.AggregatesJson })
            .ToListAsync(cancellationToken);
        var qualityByHash = siblings.ToLookup(s => s.ConfigSha256, s => QualityOf(s.AggregatesJson));

        var views = new List<ComparisonRunView>(members.Count);
        foreach (var run in members)
        {
            var configuration = RunConfiguration.Parse(run.RunConfigurationJson);
            var (aggregates, live) = await AggregatesAsync(db, run, configuration, cancellationToken);
            var figures = AggregateFigures.From(aggregates);
            views.Add(new ComparisonRunView(
                run.Id,
                run.Status,
                TargetView.From(configuration.Target),
                new PromptRefView(configuration.Prompt.Id, configuration.Prompt.Version),
                new EvaluatorRefView(configuration.Evaluator.Kind, configuration.Evaluator.Version),
                configuration.Params,
                run.ConfigSha256,
                run.CasesTotal,
                run.CasesDone,
                figures.Quality,
                figures.ErroredRate,
                figures.LatencyP50Ms,
                figures.LatencyP95Ms,
                figures.CandidateCostPerCase,
                figures.CandidateCost,
                figures.JudgeCost,
                figures.TotalCost,
                configuration.Evaluator.Kind == LlmJudgeEvaluator.KindName,
                live,
                run.Error,
                RunVariance.Of(qualityByHash[run.ConfigSha256])));
        }

        return new ComparisonDetailView(comparison.Id, comparison.TenantId, comparison.DatasetId, comparison.DatasetVersion, comparison.Name, comparison.CreatedAt, Money.Usd, views);
    }

    /// <summary>The stored aggregates of a finished run, or live ones computed from the Case Results stored so far.</summary>
    private static async Task<(JsonElement? Aggregates, bool Live)> AggregatesAsync(EvaluationDbContext db, EvaluationRun run, RunConfiguration configuration, CancellationToken cancellationToken)
    {
        if (run.AggregatesJson is { } stored)
        {
            using var document = JsonDocument.Parse(stored);
            return (document.RootElement.Clone(), false);
        }

        return RunStatusRules.IsTerminal(run.Status)
            ? (null, false)
            : (RunAggregateCalculator.ToJson(await RunAggregation.ComputeAsync(db, run.Id, configuration.Evaluator.Kind, cancellationToken)), true);
    }

    private static decimal? QualityOf(string? aggregates)
    {
        if (aggregates is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(aggregates);
        return AggregateFigures.From(document.RootElement).Quality?.Value;
    }

    internal static RunSummaryView Summary(EvaluationRun run)
    {
        var configuration = RunConfiguration.Parse(run.RunConfigurationJson);
        QualityView? quality = null;
        if (run.AggregatesJson is { } stored)
        {
            using var document = JsonDocument.Parse(stored);
            quality = AggregateFigures.From(document.RootElement).Quality;
        }

        return new RunSummaryView(
            run.Id,
            run.ComparisonId,
            run.Status,
            TargetView.From(configuration.Target),
            new PromptRefView(configuration.Prompt.Id, configuration.Prompt.Version),
            new EvaluatorRefView(configuration.Evaluator.Kind, configuration.Evaluator.Version),
            run.ConfigSha256,
            run.CasesTotal,
            run.CasesDone,
            run.CasesFailed,
            quality,
            run.MaxEstimatedCost,
            run.Error,
            run.CreatedAt,
            run.StartedAt,
            run.CompletedAt);
    }

    internal static CaseResultView ToView(CaseResult result, string externalId, int ordinal)
        => new(
            result.Id,
            result.CaseId,
            externalId,
            ordinal,
            result.Status,
            result.Score,
            result.PredictedJson is null ? null : JsonNode.Parse(result.PredictedJson),
            result.OutputText,
            JsonNode.Parse(result.ScoreDetailsJson),
            result.InputTokens,
            result.OutputTokens,
            result.EstimatedCost,
            result.JudgeInputTokens,
            result.JudgeOutputTokens,
            result.JudgeEstimatedCost,
            result.LatencyMs,
            result.ErrorCode,
            result.InferenceRequestId,
            result.JudgeInferenceRequestId,
            result.CreatedAt);
}

/// <summary>The figures the Comparison view reads from a run's aggregates JSON (<see cref="RunAggregates"/>, camelCase).</summary>
internal sealed record AggregateFigures(
    QualityView? Quality,
    decimal? ErroredRate,
    int? LatencyP50Ms,
    int? LatencyP95Ms,
    decimal? CandidateCostPerCase,
    decimal CandidateCost,
    decimal JudgeCost,
    decimal TotalCost)
{
    public static AggregateFigures From(JsonElement? aggregates)
    {
        if (aggregates is not { ValueKind: JsonValueKind.Object } root)
        {
            return new AggregateFigures(null, null, null, null, null, 0m, 0m, 0m);
        }

        QualityView? quality = root.TryGetProperty("quality", out var q) && q.ValueKind == JsonValueKind.Object
            ? new QualityView(q.GetProperty("metric").GetString()!, Decimal(q, "value"))
            : null;
        var latency = root.TryGetProperty("latency", out var l) ? l : default;
        var cost = root.TryGetProperty("cost", out var c) ? c : default;
        return new AggregateFigures(
            quality,
            Decimal(root, "erroredRate"),
            Int(latency, "p50Ms"),
            Int(latency, "p95Ms"),
            Decimal(cost, "perCaseCandidate"),
            Decimal(cost, "candidate") ?? 0m,
            Decimal(cost, "judge") ?? 0m,
            Decimal(cost, "total") ?? 0m);
    }

    private static decimal? Decimal(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : null;

    private static int? Int(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
}
