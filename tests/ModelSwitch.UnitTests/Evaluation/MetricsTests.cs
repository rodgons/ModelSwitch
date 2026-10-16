using ModelSwitch.Evaluation.Domain.Metrics;
using Shouldly;

namespace ModelSwitch.UnitTests.Evaluation;

public sealed class MetricsTests
{
    private const decimal Tolerance = 0.0000001m;

    [Fact]
    public void Accuracy_is_correct_over_total_and_null_when_there_is_nothing_to_score()
    {
        EvaluationMetrics.Accuracy(3, 4).ShouldBe(0.75m);
        EvaluationMetrics.Accuracy(0, 0).ShouldBeNull();
    }

    [Fact]
    public void Mean_is_exact_in_decimal_and_null_for_no_values()
    {
        EvaluationMetrics.Mean([1m, 0m, 0.5m]).ShouldBe(0.5m);
        EvaluationMetrics.Mean([]).ShouldBeNull();
    }

    [Fact]
    public void Rate_is_count_over_total_and_null_for_an_empty_total()
    {
        EvaluationMetrics.Rate(1, 4).ShouldBe(0.25m);
        EvaluationMetrics.Rate(0, 0).ShouldBeNull();
    }

    // Golden values from scikit-learn 1.6.1:
    //   labels = ["billing", "bug", "feature"]
    //   precision_recall_fscore_support(y_true, y_pred, labels=labels, zero_division=0)
    //     -> P [0.66666667, 0.66666667, 0.5], R [0.66666667, 0.5, 0.33333333], F1 [0.66666667, 0.57142857, 0.4], support [3, 4, 3]
    //   f1_score(y_true, y_pred, labels=labels, average="macro", zero_division=0) -> 0.546031746031746
    //   accuracy_score(y_true, y_pred) -> 0.5
    //   confusion_matrix(y_true, y_pred, labels=labels + ["unparseable"]) -> [[2,1,0,0],[0,2,1,1],[1,0,1,1],[0,0,0,0]]
    private static readonly string[] _yTrue = ["billing", "billing", "bug", "bug", "bug", "feature", "feature", "billing", "bug", "feature"];
    private static readonly string[] _yPred = ["billing", "bug", "bug", "bug", "unparseable", "feature", "billing", "billing", "feature", "unparseable"];

    [Fact]
    public void Per_class_precision_recall_and_f1_match_scikit_learn_with_zero_division_0()
    {
        var report = ClassificationMetrics.Compute(_yTrue, _yPred);

        report.Labels.ShouldBe(["billing", "bug", "feature"]);
        var billing = report.PerClass[0];
        billing.Label.ShouldBe("billing");
        billing.Precision.ShouldBe(0.6666667m, Tolerance);
        billing.Recall.ShouldBe(0.6666667m, Tolerance);
        billing.F1.ShouldBe(0.6666667m, Tolerance);
        billing.Support.ShouldBe(3);

        var bug = report.PerClass[1];
        bug.Precision.ShouldBe(0.6666667m, Tolerance);
        bug.Recall.ShouldBe(0.5m);
        bug.F1.ShouldBe(0.5714286m, Tolerance);
        bug.Support.ShouldBe(4);

        var feature = report.PerClass[2];
        feature.Precision.ShouldBe(0.5m);
        feature.Recall.ShouldBe(0.3333333m, Tolerance);
        feature.F1.ShouldBe(0.4m);
        feature.Support.ShouldBe(3);
    }

    [Fact]
    public void Macro_f1_and_accuracy_match_scikit_learn_and_unparseable_is_not_a_class()
    {
        var report = ClassificationMetrics.Compute(_yTrue, _yPred);

        report.MacroF1!.Value.ShouldBe(0.5460317m, Tolerance);
        report.Accuracy.ShouldBe(0.5m);
    }

    [Fact]
    public void Confusion_matrix_has_expected_rows_and_an_unparseable_predicted_column()
    {
        var confusion = ClassificationMetrics.Compute(_yTrue, _yPred).Confusion;

        confusion.Rows.ShouldBe(["billing", "bug", "feature"]);
        confusion.Columns.ShouldBe(["billing", "bug", "feature", "unparseable"]);
        confusion.Counts[0].ShouldBe([2, 1, 0, 0]);
        confusion.Counts[1].ShouldBe([0, 2, 1, 1]);
        confusion.Counts[2].ShouldBe([1, 0, 1, 1]);
    }

    // Golden values from scikit-learn 1.6.1:
    //   precision_recall_fscore_support(["a","a","b","b"], ["a","c","a","unparseable"], labels=["a","b","c"], zero_division=0)
    //     -> P [0.5, 0, 0], R [0.5, 0, 0], F1 [0.5, 0, 0], support [2, 2, 0]
    //   f1_score(..., labels=["a","b","c"], average="macro", zero_division=0) -> 0.16666666666666666
    [Fact]
    public void A_class_only_ever_predicted_is_a_class_and_zero_division_scores_zero()
    {
        var report = ClassificationMetrics.Compute(["a", "a", "b", "b"], ["a", "c", "a", "unparseable"]);

        report.Labels.ShouldBe(["a", "b", "c"]);
        report.PerClass[1].Precision.ShouldBe(0m);
        report.PerClass[1].F1.ShouldBe(0m);
        report.PerClass[2].Support.ShouldBe(0);
        report.PerClass[2].Recall.ShouldBe(0m);
        report.MacroF1!.Value.ShouldBe(0.1666667m, Tolerance);
    }

    [Fact]
    public void Classification_metrics_over_no_cases_are_empty_with_null_rates()
    {
        var report = ClassificationMetrics.Compute([], []);

        report.Labels.ShouldBeEmpty();
        report.MacroF1.ShouldBeNull();
        report.Accuracy.ShouldBeNull();
        report.Confusion.Columns.ShouldBe(["unparseable"]);
    }

    [Fact]
    public void Classification_inputs_must_have_the_same_length()
        => Should.Throw<ArgumentException>(() => ClassificationMetrics.Compute(["a"], []));

    [Fact]
    public void Per_field_accuracy_counts_each_field_over_the_cases_that_expected_it()
    {
        var accuracy = EvaluationMetrics.PerFieldAccuracy(
        [
            new Dictionary<string, bool> { ["name"] = true, ["email"] = true },
            new Dictionary<string, bool> { ["name"] = false, ["email"] = true },
            new Dictionary<string, bool> { ["email"] = false },
        ]);

        accuracy.Keys.ShouldBe(["email", "name"]);
        accuracy["name"].ShouldBe(0.5m);
        accuracy["email"].ShouldBe(0.6666667m, Tolerance);
    }

    [Fact]
    public void Score_distribution_has_ten_buckets_with_one_inclusive_on_the_last()
    {
        var buckets = EvaluationMetrics.ScoreDistribution([0m, 0.05m, 0.1m, 0.55m, 0.99m, 1m]);

        buckets.Count.ShouldBe(10);
        buckets[0].From.ShouldBe(0m);
        buckets[0].To.ShouldBe(0.1m);
        buckets[0].Count.ShouldBe(2);
        buckets[1].Count.ShouldBe(1);
        buckets[5].Count.ShouldBe(1);
        buckets[9].To.ShouldBe(1m);
        buckets[9].Count.ShouldBe(2);
        buckets.Sum(b => b.Count).ShouldBe(6);
    }

    [Fact]
    public void Percentiles_use_the_nearest_rank_over_the_observed_values()
    {
        int[] latencies = [500, 100, 400, 200, 300, 900, 700, 600, 800, 1000];

        EvaluationMetrics.Percentile(latencies, 0.50).ShouldBe(500);
        EvaluationMetrics.Percentile(latencies, 0.95).ShouldBe(1000);
        EvaluationMetrics.Percentile([42], 0.95).ShouldBe(42);
        EvaluationMetrics.Percentile([], 0.5).ShouldBeNull();
    }

    [Fact]
    public void Percentile_rank_out_of_range_is_a_bug()
        => Should.Throw<ArgumentOutOfRangeException>(() => EvaluationMetrics.Percentile([1], 1.5));
}
