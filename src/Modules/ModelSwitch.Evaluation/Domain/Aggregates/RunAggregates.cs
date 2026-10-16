using System.Text.Json.Serialization;
using ModelSwitch.Evaluation.Domain.Metrics;

namespace ModelSwitch.Evaluation.Domain.Aggregates;

/// <summary>
/// The aggregates of one Evaluation Run (<c>evaluation_run.aggregates</c>), serialized camelCase. Exactly one of
/// <see cref="Classification"/>, <see cref="JsonFields"/> and <see cref="LlmJudge"/> is present for those kinds; none for
/// <c>exact_match</c> and <c>reference_similarity</c>.
/// <c>CasesTotal</c> counts every Case Result whatever its status; <c>Scored</c> is passed plus failed, the Cases quality is
/// computed over; <c>ErroredRate</c> is errored over attempted (scored plus errored) Cases, since skipped Cases were never
/// attempted, and null when none was attempted.
/// </summary>
internal sealed record RunAggregates(
    string EvaluatorKind,
    int CasesTotal,
    int Scored,
    int Passed,
    int Failed,
    int Errored,
    int Skipped,
    decimal? ErroredRate,
    QualityAggregate Quality,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClassificationAggregate? Classification,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonFieldsAggregate? JsonFields,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LlmJudgeAggregate? LlmJudge,
    LatencyAggregate Latency,
    TokenAggregate Tokens,
    CostAggregate Cost,
    string Currency);

/// <summary>
/// The run's headline quality number over scored Cases (failed included), or null when nothing was scored. <c>Metric</c> is
/// <c>accuracy</c>, <c>meanScore</c> or <c>meanTokenF1</c>, depending on the Evaluator kind.
/// </summary>
internal sealed record QualityAggregate(string Metric, decimal? Value);

internal sealed record ClassificationAggregate(IReadOnlyList<ClassMetrics> PerClass, decimal? MacroF1, ConfusionMatrix Confusion);

/// <param name="PerFieldAccuracy">Per expected field, the fraction of scored Cases that matched it (parse failures count as misses).</param>
/// <param name="ParseFailureRate">Unparseable outputs over scored Cases.</param>
internal sealed record JsonFieldsAggregate(IReadOnlyDictionary<string, decimal> PerFieldAccuracy, decimal? ParseFailureRate);

/// <param name="Distribution">Ten 0.1-wide buckets of the scored Cases' judge scores.</param>
/// <param name="ModelJudged">Always true: the UI labels these results as judged by a model.</param>
internal sealed record LlmJudgeAggregate(IReadOnlyList<ScoreBucket> Distribution, bool ModelJudged);

/// <summary>Exact nearest-rank percentiles over every attempted Case that has a latency.</summary>
internal sealed record LatencyAggregate(int? P50Ms, int? P95Ms);

internal sealed record TokenAggregate(long CandidateInput, long CandidateOutput, long JudgeInput, long JudgeOutput);

/// <summary>
/// Estimated Cost sums. The Comparison view lines configurations up on <see cref="Candidate"/>; judge cost is separate.
/// <c>PerCaseCandidate</c> is candidate cost over the Cases that have one (null when none does).
/// </summary>
internal sealed record CostAggregate(decimal Candidate, decimal Judge, decimal Total, decimal? PerCaseCandidate);
