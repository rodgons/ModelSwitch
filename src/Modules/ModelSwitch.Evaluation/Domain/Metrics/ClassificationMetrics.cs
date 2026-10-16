namespace ModelSwitch.Evaluation.Domain.Metrics;

/// <summary>Precision, recall and F1 of one class, and its support (the number of Cases whose expected label it is).</summary>
internal sealed record ClassMetrics(string Label, decimal Precision, decimal Recall, decimal F1, int Support);

/// <summary>
/// Counts of expected label (row) by predicted label (column). The columns are the classes followed by
/// <see cref="ClassificationMetrics.Unparseable"/>, so an output that matched no label is visible rather than dropped.
/// </summary>
internal sealed record ConfusionMatrix(IReadOnlyList<string> Rows, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<int>> Counts);

internal sealed record ClassificationReport(
    IReadOnlyList<string> Labels,
    IReadOnlyList<ClassMetrics> PerClass,
    decimal? MacroF1,
    decimal? Accuracy,
    ConfusionMatrix Confusion);

/// <summary>
/// Multi-class classification metrics with scikit-learn semantics: <c>precision_recall_fscore_support(y_true, y_pred,
/// labels=L, zero_division=0)</c> and <c>f1_score(..., average="macro")</c>, where <c>L</c> is the ordinal-sorted union of the
/// expected and predicted labels minus <see cref="Unparseable"/>. An unparseable prediction is a miss for its expected class
/// (lowering recall) but is not itself a class, so it does not add a zero to the macro average.
/// </summary>
internal static class ClassificationMetrics
{
    /// <summary>The predicted value of an output that matched no label. It can never be a configured label.</summary>
    public const string Unparseable = "unparseable";

    public static ClassificationReport Compute(IReadOnlyList<string> expected, IReadOnlyList<string> predicted)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(predicted);
        if (expected.Count != predicted.Count)
        {
            throw new ArgumentException("Expected and predicted labels must have the same length.", nameof(predicted));
        }

        var labels = expected.Concat(predicted)
            .Where(label => !string.Equals(label, Unparseable, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var columns = labels.Append(Unparseable).ToArray();
        var columnIndex = columns.Select((label, i) => (label, i)).ToDictionary(x => x.label, x => x.i, StringComparer.Ordinal);

        var counts = new int[labels.Length][];
        for (var i = 0; i < labels.Length; i++)
        {
            counts[i] = new int[columns.Length];
        }

        var correct = 0;
        for (var i = 0; i < expected.Count; i++)
        {
            if (string.Equals(expected[i], predicted[i], StringComparison.Ordinal))
            {
                correct++;
            }

            // An expected label of "unparseable" is impossible (it is never a configured label); skip it defensively.
            if (columnIndex.TryGetValue(expected[i], out var row) && row < labels.Length)
            {
                counts[row][columnIndex[predicted[i]]]++;
            }
        }

        var perClass = new ClassMetrics[labels.Length];
        for (var k = 0; k < labels.Length; k++)
        {
            var truePositives = counts[k][k];
            var support = counts[k].Sum();
            var predictedAsK = expected.Count == 0 ? 0 : counts.Sum(r => r[k]);
            var falsePositives = predictedAsK - truePositives;
            var falseNegatives = support - truePositives;
            perClass[k] = new ClassMetrics(
                labels[k],
                Divide(truePositives, predictedAsK),
                Divide(truePositives, support),
                Divide(2 * truePositives, (2 * truePositives) + falsePositives + falseNegatives),
                support);
        }

        return new ClassificationReport(
            labels,
            perClass,
            perClass.Length == 0 ? null : perClass.Sum(c => c.F1) / perClass.Length,
            EvaluationMetrics.Accuracy(correct, expected.Count),
            new ConfusionMatrix(labels, columns, counts));
    }

    /// <summary>zero_division=0: a ratio with an empty denominator is 0.</summary>
    private static decimal Divide(int numerator, int denominator) => denominator == 0 ? 0m : (decimal)numerator / denominator;
}
