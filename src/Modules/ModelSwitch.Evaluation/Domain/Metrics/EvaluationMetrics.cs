namespace ModelSwitch.Evaluation.Domain.Metrics;

/// <summary>One bucket of a score histogram: scores in <c>[From, To)</c>, or <c>[From, To]</c> for the last bucket.</summary>
internal sealed record ScoreBucket(decimal From, decimal To, int Count);

/// <summary>
/// The general evaluation metrics (evaluation.md section 3), as pure functions. Everything is <c>decimal</c> so a result does
/// not depend on the order of its inputs, and a metric over nothing is <c>null</c> rather than 0 or NaN.
/// </summary>
internal static class EvaluationMetrics
{
    /// <summary>Fraction of correct Cases, or null when <paramref name="total"/> is 0.</summary>
    public static decimal? Accuracy(int correct, int total) => Rate(correct, total);

    /// <summary><paramref name="count"/> over <paramref name="total"/>, or null when <paramref name="total"/> is 0 (for example <c>parse_failure_rate</c>).</summary>
    public static decimal? Rate(int count, int total)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        return total == 0 ? null : (decimal)count / total;
    }

    /// <summary>Arithmetic mean, or null for no values.</summary>
    public static decimal? Mean(IReadOnlyCollection<decimal> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Count == 0 ? null : values.Sum() / values.Count;
    }

    /// <summary>
    /// Accuracy of each field over the Cases whose expected result had that field. Each element is one Case's
    /// field-to-matched map. Fields come back in ordinal order.
    /// </summary>
    public static IReadOnlyDictionary<string, decimal> PerFieldAccuracy(IEnumerable<IReadOnlyDictionary<string, bool>> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        var tally = new SortedDictionary<string, (int Matched, int Total)>(StringComparer.Ordinal);
        foreach (var fields in cases)
        {
            foreach (var (field, matched) in fields)
            {
                var (m, t) = tally.GetValueOrDefault(field);
                tally[field] = (m + (matched ? 1 : 0), t + 1);
            }
        }

        var result = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (field, (matched, total)) in tally)
        {
            result[field] = (decimal)matched / total;
        }

        return result;
    }

    /// <summary>
    /// A histogram of 0-1 scores in <paramref name="buckets"/> equal-width buckets (10 by default: 0-0.1 … 0.9-1). Each bucket
    /// holds <c>[From, To)</c>; the last also holds 1. Scores outside 0-1 are clamped into the first or last bucket.
    /// </summary>
    public static IReadOnlyList<ScoreBucket> ScoreDistribution(IEnumerable<decimal> scores, int buckets = 10)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentOutOfRangeException.ThrowIfLessThan(buckets, 1);
        var counts = new int[buckets];
        foreach (var score in scores)
        {
            var index = (int)Math.Floor(score * buckets);
            counts[Math.Clamp(index, 0, buckets - 1)]++;
        }

        var result = new ScoreBucket[buckets];
        for (var i = 0; i < buckets; i++)
        {
            result[i] = new ScoreBucket((decimal)i / buckets, (decimal)(i + 1) / buckets, counts[i]);
        }

        return result;
    }

    /// <summary>
    /// The exact <paramref name="quantile"/> (0 to 1) by the nearest-rank method: the smallest observed value with at least
    /// <c>ceil(quantile × n)</c> values at or below it. Always one of the inputs. Null for no values.
    /// </summary>
    public static int? Percentile(IReadOnlyCollection<int> values, double quantile)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (quantile is < 0 or > 1 || double.IsNaN(quantile))
        {
            throw new ArgumentOutOfRangeException(nameof(quantile), quantile, "The quantile must be between 0 and 1.");
        }

        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.Order().ToArray();
        var rank = (int)Math.Ceiling(quantile * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }
}
