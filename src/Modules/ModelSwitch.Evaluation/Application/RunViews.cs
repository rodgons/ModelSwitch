#pragma warning disable CS1573 // Record parameters are documented only where they need explanation.

using System.Text.Json.Nodes;
using ModelSwitch.Evaluation.Domain.Runs;

namespace ModelSwitch.Evaluation.Application;

/// <summary>202 answer of <c>POST /admin/evaluations</c> and of "Run again": the new Comparison and its runs, in configuration order.</summary>
internal sealed record CreatedEvaluationView(Guid ComparisonId, IReadOnlyList<Guid> RunIds);

/// <summary>A run's frozen target: <c>label</c> is <c>alias@vN</c> or <c>provider/model</c>.</summary>
internal sealed record TargetView(string Label, string? Alias, Guid? PolicyId, int? PolicyVersion, Guid? ProviderModelId, string? ProviderModel)
{
    public static TargetView From(FrozenTarget target)
        => new(target.Label, target.Alias, target.PolicyId, target.PolicyVersion, target.ProviderModelId, target.ProviderModel);
}

internal sealed record PromptRefView(Guid Id, int Version);

internal sealed record EvaluatorRefView(string Kind, string Version);

/// <summary>The run's headline quality number (<c>accuracy</c>, <c>meanScore</c> or <c>meanTokenF1</c>), null when nothing was scored.</summary>
internal sealed record QualityView(string Metric, decimal? Value);

/// <summary>One Evaluation Run in a list.</summary>
/// <param name="CasesDone">Case Results stored so far, whatever their status (skipped included).</param>
/// <param name="CasesFailed">Case Results that were attempted and did not pass: failed plus errored.</param>
/// <param name="Quality">From the stored aggregates; null until the run has them.</param>
/// <param name="Error">A closed-set code when the run failed (<c>run_cost_cap_reached</c>, <c>internal_error</c>).</param>
internal sealed record RunSummaryView(
    Guid Id,
    Guid ComparisonId,
    string Status,
    TargetView Target,
    PromptRefView Prompt,
    EvaluatorRefView Evaluator,
    string ConfigSha256,
    int CasesTotal,
    int CasesDone,
    int CasesFailed,
    QualityView? Quality,
    decimal? MaxEstimatedCost,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>A Comparison with its runs (in configuration order), as <c>GET /admin/evaluations</c> lists it.</summary>
internal sealed record ComparisonView(
    Guid Id,
    Guid TenantId,
    Guid DatasetId,
    int DatasetVersion,
    string Name,
    DateTimeOffset CreatedAt,
    Guid? CreatedByAdminId,
    IReadOnlyList<RunSummaryView> Runs);

/// <param name="Total">The Dataset Version's Case count.</param>
/// <param name="Done">Case Results stored (any status).</param>
/// <param name="Failed">Failed plus errored Case Results.</param>
internal sealed record ProgressView(int Total, int Done, int Failed);

/// <summary>"What exactly produced this number?" (evaluation.md section 6). The Provider Model of each Case is on its Inference Request.</summary>
internal sealed record ReproducibilityView(
    Guid DatasetId,
    int DatasetVersion,
    string ContentSha256,
    PromptRefView Prompt,
    TargetView Target,
    GenerationParams Params,
    FrozenEvaluator Evaluator,
    string ConfigSha256,
    string GatewayVersion,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>Case Result counts by status and, for the ones that have one, by <c>error_code</c>.</summary>
internal sealed record FailureSummaryView(IReadOnlyDictionary<string, int> ByStatus, IReadOnlyDictionary<string, int> ByErrorCode);

/// <summary><c>GET /admin/evaluations/{runId}</c>.</summary>
/// <param name="Configuration">The frozen Run Configuration as stored, Prompt Version template included.</param>
/// <param name="Aggregates">The stored aggregates of a finished run; for a queued or running run, computed now from the Case Results stored so far.</param>
/// <param name="AggregatesLive">True when <paramref name="Aggregates"/> were computed for this answer rather than read from the run.</param>
internal sealed record RunDetailView(
    Guid Id,
    Guid ComparisonId,
    string ComparisonName,
    Guid TenantId,
    string Status,
    ProgressView Progress,
    JsonNode? Configuration,
    string ConfigSha256,
    decimal? MaxEstimatedCost,
    ReproducibilityView Reproducibility,
    JsonNode? Aggregates,
    bool AggregatesLive,
    FailureSummaryView Failures,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>One Case Result (<c>GET /admin/evaluations/{runId}/results</c>). Token and cost columns are the candidate call's; <c>judge*</c> the <c>llm_judge</c> call's.</summary>
/// <param name="OutputText">The candidate's output (stored by design for evaluation, ADR-0010); null when no call succeeded.</param>
internal sealed record CaseResultView(
    Guid Id,
    Guid CaseId,
    string ExternalId,
    int Ordinal,
    string Status,
    decimal? Score,
    JsonNode? Predicted,
    string? OutputText,
    JsonNode? ScoreDetails,
    int? InputTokens,
    int? OutputTokens,
    decimal? EstimatedCost,
    int? JudgeInputTokens,
    int? JudgeOutputTokens,
    decimal? JudgeEstimatedCost,
    int? LatencyMs,
    string? ErrorCode,
    Guid? InferenceRequestId,
    Guid? JudgeInferenceRequestId,
    DateTimeOffset CreatedAt);

/// <summary>
/// The cost of one Run Configuration if every Case cost its worst case (evaluation.md section 4): the Budget Reservation of each
/// Case's candidate call, plus its judge call for <c>llm_judge</c>.
/// </summary>
/// <param name="EstimatedCases">Cases that could be costed; the others are counted in <paramref name="Failures"/> by error code and cost nothing.</param>
/// <param name="MaxPerCaseWorstCase">The most expensive Case; null when none could be costed.</param>
/// <param name="ExceedsMaxEstimatedCost">Whether <paramref name="WorstCase"/> is above the request's <c>maxEstimatedCost</c>; null without one.</param>
internal sealed record ConfigurationEstimateView(
    int Index,
    string Target,
    int Cases,
    int EstimatedCases,
    decimal CandidateWorstCase,
    decimal JudgeWorstCase,
    decimal WorstCase,
    decimal? MaxPerCaseWorstCase,
    bool? ExceedsMaxEstimatedCost,
    IReadOnlyDictionary<string, int> Failures);

/// <summary><c>POST /admin/evaluations/estimate</c>: per configuration and in total, USD.</summary>
internal sealed record EvaluationEstimateView(string Currency, decimal Total, IReadOnlyList<ConfigurationEstimateView> Configurations);

/// <summary>One run in the side-by-side view (<c>GET /admin/comparisons/{id}</c>).</summary>
/// <param name="CandidateCostPerCase">The comparable number: candidate Estimated Cost per Case that has one.</param>
/// <param name="JudgeCost">Shown as its own labelled column, never folded into the candidate cost.</param>
/// <param name="ModelJudged">True for <c>llm_judge</c>: the quality is a model's judgement.</param>
/// <param name="Variance">Run-to-run variance of the quality over this Tenant's finished runs with the same <paramref name="ConfigSha256"/>; null below two runs.</param>
internal sealed record ComparisonRunView(
    Guid RunId,
    string Status,
    TargetView Target,
    PromptRefView Prompt,
    EvaluatorRefView Evaluator,
    GenerationParams Params,
    string ConfigSha256,
    int CasesTotal,
    int CasesDone,
    QualityView? Quality,
    decimal? ErroredRate,
    int? LatencyP50Ms,
    int? LatencyP95Ms,
    decimal? CandidateCostPerCase,
    decimal CandidateCost,
    decimal JudgeCost,
    decimal TotalCost,
    bool ModelJudged,
    bool AggregatesLive,
    string? Error,
    RunVariance? Variance);

internal sealed record ComparisonDetailView(
    Guid Id,
    Guid TenantId,
    Guid DatasetId,
    int DatasetVersion,
    string Name,
    DateTimeOffset CreatedAt,
    string Currency,
    IReadOnlyList<ComparisonRunView> Runs);

/// <summary>The answer of <c>POST /admin/evaluations/{runId}/cancel</c>.</summary>
internal sealed record CancelledRunView(Guid Id, string Status);
