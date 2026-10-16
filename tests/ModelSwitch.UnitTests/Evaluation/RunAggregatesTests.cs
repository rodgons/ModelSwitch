using System.Text.Json;
using ModelSwitch.Evaluation.Domain.Aggregates;
using ModelSwitch.Evaluation.Domain.Evaluators;
using Shouldly;
using static ModelSwitch.UnitTests.Evaluation.EvaluatorTestSupport;

namespace ModelSwitch.UnitTests.Evaluation;

public sealed class RunAggregatesTests
{
    private static CaseResultRecord Scored(
        CaseStatus status,
        decimal score,
        string? predicted = null,
        string? expected = null,
        string details = "{}",
        int latencyMs = 100,
        decimal cost = 0.001m,
        int inputTokens = 10,
        int outputTokens = 5)
        => new(status, score, predicted is null ? null : Json(predicted), expected is null ? null : Json(expected), Json(details), latencyMs, inputTokens, outputTokens, cost);

    private static CaseResultRecord Errored(int? latencyMs = 50, decimal? cost = null)
        => new(CaseStatus.Errored, null, null, null, Json("""{"error":"x"}"""), latencyMs, null, null, cost) { ErrorCode = "provider_error" };

    private static CaseResultRecord Skipped() => new(CaseStatus.Skipped, null, null, null, Json("{}"), null, null, null, null);

    [Fact]
    public void Counts_split_cases_by_status_and_the_errored_rate_is_over_attempted_cases()
    {
        var aggregates = RunAggregateCalculator.Compute("exact_match",
        [
            Scored(CaseStatus.Passed, 1m), Scored(CaseStatus.Failed, 0m), Scored(CaseStatus.Passed, 1m), Errored(), Skipped(),
        ]);

        aggregates.CasesTotal.ShouldBe(5);
        aggregates.Scored.ShouldBe(3);
        aggregates.Passed.ShouldBe(2);
        aggregates.Failed.ShouldBe(1);
        aggregates.Errored.ShouldBe(1);
        aggregates.Skipped.ShouldBe(1);
        aggregates.ErroredRate.ShouldBe(0.25m);
    }

    [Fact]
    public void Quality_is_over_scored_cases_with_failed_included_and_errored_and_skipped_excluded()
    {
        var aggregates = RunAggregateCalculator.Compute("exact_match",
            [Scored(CaseStatus.Passed, 1m), Scored(CaseStatus.Failed, 0m), Errored(), Errored(), Skipped()]);

        aggregates.Quality.Metric.ShouldBe("accuracy");
        aggregates.Quality.Value.ShouldBe(0.5m);
    }

    [Theory]
    [InlineData("exact_match", "accuracy")]
    [InlineData("classification", "accuracy")]
    [InlineData("json_fields", "meanScore")]
    [InlineData("llm_judge", "meanScore")]
    [InlineData("reference_similarity", "meanTokenF1")]
    public void Each_evaluator_kind_names_its_quality_metric(string kind, string metric)
        => RunAggregateCalculator.Compute(kind, []).Quality.Metric.ShouldBe(metric);

    [Fact]
    public void A_run_with_nothing_scored_has_null_quality_and_rates()
    {
        var aggregates = RunAggregateCalculator.Compute("json_fields", [Skipped()]);

        aggregates.Quality.Value.ShouldBeNull();
        aggregates.ErroredRate.ShouldBeNull();
        aggregates.Latency.P50Ms.ShouldBeNull();
        aggregates.Cost.PerCaseCandidate.ShouldBeNull();
        aggregates.JsonFields!.ParseFailureRate.ShouldBeNull();
    }

    [Fact]
    public void Mean_score_quality_is_rounded_to_six_decimals()
    {
        var aggregates = RunAggregateCalculator.Compute("json_fields",
        [
            Scored(CaseStatus.Passed, 1m, details: """{"parseFailure":false,"fields":{}}"""),
            Scored(CaseStatus.Failed, 0.5m, details: """{"parseFailure":false,"fields":{}}"""),
            Scored(CaseStatus.Failed, 0m, details: """{"parseFailure":true,"fields":{}}"""),
        ]);

        aggregates.Quality.Value.ShouldBe(0.5m);
        RunAggregateCalculator.Compute("reference_similarity",
            [Scored(CaseStatus.Passed, 1m), Scored(CaseStatus.Failed, 0m), Scored(CaseStatus.Failed, 0m)]).Quality.Value.ShouldBe(0.333333m);
    }

    [Fact]
    public void Classification_reports_per_class_metrics_macro_f1_and_a_confusion_matrix_with_unparseable()
    {
        // Same fixture as MetricsTests (scikit-learn golden values): macro-F1 0.546031746..., accuracy 0.5.
        string[] yTrue = ["billing", "billing", "bug", "bug", "bug", "feature", "feature", "billing", "bug", "feature"];
        string[] yPred = ["billing", "bug", "bug", "bug", "unparseable", "feature", "billing", "billing", "feature", "unparseable"];
        var records = yTrue.Zip(yPred)
            .Select(p => Scored(p.First == p.Second ? CaseStatus.Passed : CaseStatus.Failed, p.First == p.Second ? 1m : 0m, $"\"{p.Second}\"", $"\"{p.First}\""))
            .Append(Errored())
            .ToArray();

        var aggregates = RunAggregateCalculator.Compute("classification", records);

        aggregates.Quality.Value.ShouldBe(0.5m);
        var classification = aggregates.Classification.ShouldNotBeNull();
        classification.MacroF1.ShouldBe(0.546032m);
        classification.PerClass.Select(c => c.Label).ShouldBe(["billing", "bug", "feature"]);
        classification.PerClass[1].F1.ShouldBe(0.571429m);
        classification.PerClass[1].Support.ShouldBe(4);
        classification.Confusion.Columns.ShouldBe(["billing", "bug", "feature", "unparseable"]);
        classification.Confusion.Counts[2].ShouldBe([1, 0, 1, 1]);
        aggregates.JsonFields.ShouldBeNull();
        aggregates.LlmJudge.ShouldBeNull();
    }

    [Fact]
    public void Json_fields_reports_per_field_accuracy_and_the_parse_failure_rate_over_scored_cases()
    {
        var aggregates = RunAggregateCalculator.Compute("json_fields",
        [
            Scored(CaseStatus.Passed, 1m, details: """{"parseFailure":false,"fields":{"name":true,"email":true}}"""),
            Scored(CaseStatus.Failed, 0.5m, details: """{"parseFailure":false,"fields":{"name":false,"email":true}}"""),
            Scored(CaseStatus.Failed, 0m, details: """{"parseFailure":true,"fields":{"name":false,"email":false}}"""),
            Scored(CaseStatus.Failed, 0.5m, details: """{"parseFailure":false,"fields":{"name":true,"email":false}}"""),
            Errored(),
        ]);

        var jsonFields = aggregates.JsonFields.ShouldNotBeNull();
        jsonFields.ParseFailureRate.ShouldBe(0.25m);
        jsonFields.PerFieldAccuracy["name"].ShouldBe(0.5m);
        jsonFields.PerFieldAccuracy["email"].ShouldBe(0.5m);
        aggregates.Quality.Value.ShouldBe(0.5m);
    }

    [Fact]
    public void Llm_judge_reports_a_score_distribution_and_is_labelled_model_judged()
    {
        var aggregates = RunAggregateCalculator.Compute("llm_judge",
            [Scored(CaseStatus.Passed, 0.8m), Scored(CaseStatus.Passed, 1m), Scored(CaseStatus.Failed, 0.2m), Errored()]);

        var judge = aggregates.LlmJudge.ShouldNotBeNull();
        judge.ModelJudged.ShouldBeTrue();
        judge.Distribution.Count.ShouldBe(10);
        judge.Distribution.Sum(b => b.Count).ShouldBe(3);
        judge.Distribution[8].Count.ShouldBe(1);
        judge.Distribution[9].Count.ShouldBe(1);
        judge.Distribution[2].Count.ShouldBe(1);
    }

    [Fact]
    public void Latency_percentiles_are_exact_over_every_attempted_case_with_a_latency()
    {
        var records = Enumerable.Range(1, 9).Select(i => Scored(CaseStatus.Passed, 1m, latencyMs: i * 100)).Append(Errored(latencyMs: 1000)).Append(Skipped()).ToArray();

        var latency = RunAggregateCalculator.Compute("exact_match", records).Latency;

        latency.P50Ms.ShouldBe(500);
        latency.P95Ms.ShouldBe(1000);
    }

    [Fact]
    public void Tokens_and_costs_sum_candidate_and_judge_separately_with_a_per_case_candidate_cost()
    {
        var withJudge = Scored(CaseStatus.Passed, 1m, cost: 0.002m, inputTokens: 100, outputTokens: 20) with
        {
            JudgeInputTokens = 300,
            JudgeOutputTokens = 30,
            JudgeEstimatedCost = 0.0005m,
        };
        var aggregates = RunAggregateCalculator.Compute("llm_judge",
            [withJudge, Scored(CaseStatus.Failed, 0m, cost: 0.001m, inputTokens: 50, outputTokens: 10), Errored(cost: null), Skipped()]);

        aggregates.Tokens.CandidateInput.ShouldBe(150);
        aggregates.Tokens.CandidateOutput.ShouldBe(30);
        aggregates.Tokens.JudgeInput.ShouldBe(300);
        aggregates.Tokens.JudgeOutput.ShouldBe(30);
        aggregates.Cost.Candidate.ShouldBe(0.003m);
        aggregates.Cost.Judge.ShouldBe(0.0005m);
        aggregates.Cost.Total.ShouldBe(0.0035m);
        aggregates.Cost.PerCaseCandidate.ShouldBe(0.0015m);
        aggregates.Currency.ShouldBe("USD");
    }

    [Fact]
    public void Aggregates_serialize_as_camel_case_json_without_other_kinds_blocks()
    {
        var json = RunAggregateCalculator.ToJson(RunAggregateCalculator.Compute("exact_match", [Scored(CaseStatus.Passed, 1m)]));

        json.GetProperty("casesTotal").GetInt32().ShouldBe(1);
        json.GetProperty("erroredRate").GetDecimal().ShouldBe(0m);
        json.GetProperty("quality").GetProperty("metric").GetString().ShouldBe("accuracy");
        json.GetProperty("latency").GetProperty("p50Ms").GetInt32().ShouldBe(100);
        json.GetProperty("cost").GetProperty("perCaseCandidate").GetDecimal().ShouldBe(0.001m);
        json.GetProperty("tokens").GetProperty("candidateInput").GetInt64().ShouldBe(10);
        json.GetProperty("currency").GetString().ShouldBe("USD");
        json.TryGetProperty("classification", out _).ShouldBeFalse();
        json.TryGetProperty("jsonFields", out _).ShouldBeFalse();
    }

    [Fact]
    public void Aggregates_are_deterministic_whatever_the_order_of_the_case_results()
    {
        CaseResultRecord[] records =
        [
            Scored(CaseStatus.Passed, 1m, "\"a\"", "\"a\"", latencyMs: 30, cost: 0.0000001m),
            Scored(CaseStatus.Failed, 0m, "\"unparseable\"", "\"b\"", latencyMs: 10, cost: 0.3333333333m),
            Scored(CaseStatus.Failed, 0m, "\"a\"", "\"c\"", latencyMs: 20, cost: 1m),
            Errored(),
        ];

        var forward = RunAggregateCalculator.ToJson(RunAggregateCalculator.Compute("classification", records)).GetRawText();
        var backward = RunAggregateCalculator.ToJson(RunAggregateCalculator.Compute("classification", records.Reverse().ToArray())).GetRawText();

        backward.ShouldBe(forward);
    }

    [Fact]
    public void An_unknown_evaluator_kind_is_a_bug()
        => Should.Throw<ArgumentOutOfRangeException>(() => RunAggregateCalculator.Compute("bleu", []));

    [Fact]
    public void Aggregates_round_trip_through_the_score_from_an_evaluator()
    {
        // The executor stores CaseScore fields; aggregates read them back as stored. This is the glue the run uses.
        var score = Score("classification", """{"labels":["a","b"]}""", "\"a\"", "b");
        var record = CaseResultRecord.From(score, Json("\"a\""), latencyMs: 12, inputTokens: 1, outputTokens: 1, estimatedCost: 0m);

        var aggregates = RunAggregateCalculator.Compute("classification", [record]);

        aggregates.Failed.ShouldBe(1);
        aggregates.Classification!.Confusion.Counts[0].ShouldBe([0, 1, 0]);
        JsonSerializer.Serialize(aggregates.Classification.Confusion.Columns).ShouldBe("""["a","b","unparseable"]""");
    }
}
