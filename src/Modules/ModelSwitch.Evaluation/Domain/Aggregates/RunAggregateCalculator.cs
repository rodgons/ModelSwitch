using System.Text.Json;
using ModelSwitch.Evaluation.Domain.Evaluators;
using ModelSwitch.Evaluation.Domain.Metrics;
using ModelSwitch.SharedKernel;

namespace ModelSwitch.Evaluation.Domain.Aggregates;

/// <summary>
/// Computes a run's aggregates from its stored Case Results only, so they can be recomputed on demand (FR-09). The result does
/// not depend on the order of the records: sums are exact <c>decimal</c>, labels and fields are sorted ordinally.
/// </summary>
/// <remarks>
/// Rules: quality is over scored Cases (passed plus failed; an unparseable output is a failed Case and counts), errored and
/// skipped Cases are excluded from quality and reported through <see cref="RunAggregates.ErroredRate"/> and the counts.
/// Rates and quality values are rounded to <see cref="MetricScale"/> places; money to <see cref="Money.Scale"/>.
/// </remarks>
internal static class RunAggregateCalculator
{
    /// <summary>Decimal places of every rate and quality number in the aggregates.</summary>
    public const int MetricScale = 6;

    public static RunAggregates Compute(string evaluatorKind, IReadOnlyList<CaseResultRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var metric = QualityMetricOf(evaluatorKind);
        var scored = records.Where(r => r.Status is CaseStatus.Passed or CaseStatus.Failed).ToArray();
        var passed = records.Count(r => r.Status == CaseStatus.Passed);
        var errored = records.Count(r => r.Status == CaseStatus.Errored);
        var skipped = records.Count(r => r.Status == CaseStatus.Skipped);
        var scores = scored.Select(r => r.Score ?? 0m).ToArray();

        var quality = metric == "accuracy"
            ? EvaluationMetrics.Accuracy(scores.Count(s => s == 1m), scores.Length)
            : EvaluationMetrics.Mean(scores);

        var attempted = records.Where(r => r.Status != CaseStatus.Skipped).ToArray();
        var latencies = attempted.Where(r => r.LatencyMs is not null).Select(r => r.LatencyMs!.Value).ToArray();
        var candidateCosts = records.Where(r => r.EstimatedCost is not null).Select(r => r.EstimatedCost!.Value).ToArray();
        var candidate = candidateCosts.Sum();
        var judge = records.Sum(r => r.JudgeEstimatedCost ?? 0m);

        return new RunAggregates(
            evaluatorKind,
            records.Count,
            scored.Length,
            passed,
            scored.Length - passed,
            errored,
            skipped,
            RoundMetric(EvaluationMetrics.Rate(errored, scored.Length + errored)),
            new QualityAggregate(metric, RoundMetric(quality)),
            evaluatorKind == ClassificationEvaluator.KindName ? Classification(scored) : null,
            evaluatorKind == JsonFieldsEvaluator.KindName ? JsonFields(scored) : null,
            evaluatorKind == LlmJudgeEvaluator.KindName ? new LlmJudgeAggregate(EvaluationMetrics.ScoreDistribution(scores), true) : null,
            new LatencyAggregate(EvaluationMetrics.Percentile(latencies, 0.50), EvaluationMetrics.Percentile(latencies, 0.95)),
            new TokenAggregate(
                records.Sum(r => (long)(r.InputTokens ?? 0)),
                records.Sum(r => (long)(r.OutputTokens ?? 0)),
                records.Sum(r => (long)(r.JudgeInputTokens ?? 0)),
                records.Sum(r => (long)(r.JudgeOutputTokens ?? 0))),
            new CostAggregate(
                RoundMoney(candidate),
                RoundMoney(judge),
                RoundMoney(candidate + judge),
                candidateCosts.Length == 0 ? null : RoundMoney(candidate / candidateCosts.Length)),
            Money.Usd);
    }

    public static JsonElement ToJson(RunAggregates aggregates) => EvaluationJson.ToElement(aggregates);

    private static string QualityMetricOf(string evaluatorKind) => evaluatorKind switch
    {
        ExactMatchEvaluator.KindName or ClassificationEvaluator.KindName => "accuracy",
        JsonFieldsEvaluator.KindName or LlmJudgeEvaluator.KindName => "meanScore",
        ReferenceSimilarityEvaluator.KindName => "meanTokenF1",
        _ => throw new ArgumentOutOfRangeException(nameof(evaluatorKind), evaluatorKind, "Unknown Evaluator kind."),
    };

    private static ClassificationAggregate Classification(CaseResultRecord[] scored)
    {
        var expected = scored.Select(r => Label(r.Expected)).ToArray();
        var predicted = scored.Select(r => Label(r.Predicted)).ToArray();
        var report = ClassificationMetrics.Compute(expected, predicted);
        var perClass = report.PerClass
            .Select(c => c with { Precision = Round(c.Precision), Recall = Round(c.Recall), F1 = Round(c.F1) })
            .ToArray();
        return new ClassificationAggregate(perClass, RoundMetric(report.MacroF1), report.Confusion);
    }

    /// <summary>A stored label: a JSON string; anything else (never written by <c>classification</c> v1) is its JSON text.</summary>
    private static string Label(JsonElement? value) => value switch
    {
        null => ClassificationMetrics.Unparseable,
        { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => ClassificationMetrics.Unparseable,
        { } element => EvaluationJson.Text(element),
    };

    private static JsonFieldsAggregate JsonFields(CaseResultRecord[] scored)
    {
        var perCase = new List<IReadOnlyDictionary<string, bool>>(scored.Length);
        var parseFailures = 0;
        foreach (var record in scored)
        {
            var fields = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (record.ScoreDetails is { ValueKind: JsonValueKind.Object } details)
            {
                if (details.TryGetProperty("parseFailure", out var failure) && failure.ValueKind == JsonValueKind.True)
                {
                    parseFailures++;
                }

                if (details.TryGetProperty("fields", out var matched) && matched.ValueKind == JsonValueKind.Object)
                {
                    foreach (var field in matched.EnumerateObject())
                    {
                        fields[field.Name] = field.Value.ValueKind == JsonValueKind.True;
                    }
                }
            }

            perCase.Add(fields);
        }

        var perField = EvaluationMetrics.PerFieldAccuracy(perCase)
            .ToDictionary(f => f.Key, f => Round(f.Value), StringComparer.Ordinal);
        return new JsonFieldsAggregate(
            new SortedDictionary<string, decimal>(perField, StringComparer.Ordinal),
            RoundMetric(EvaluationMetrics.Rate(parseFailures, scored.Length)));
    }

    private static decimal Round(decimal value) => decimal.Round(value, MetricScale, MidpointRounding.ToEven);

    private static decimal? RoundMetric(decimal? value) => value is { } v ? Round(v) : null;

    private static decimal RoundMoney(decimal value) => decimal.Round(value, Money.Scale, MidpointRounding.ToEven);
}
