namespace ModelSwitch.Usage.Domain;

/// <summary>
/// The fixed latency buckets of <c>usage_hourly</c> (data-model.md, usage-and-budgets.md section 6). Every hour row counts its requests
/// over the same bounds, so rows merge by adding bucket by bucket and a percentile over any range is estimated as the upper bound of
/// the bucket that holds it. Used for duration and for TTFT.
/// </summary>
internal static class LatencyHistogram
{
    /// <summary>Inclusive upper bounds in milliseconds. A last bucket counts everything above the final bound.</summary>
    public static IReadOnlyList<int> UpperBoundsMs { get; } = [50, 100, 250, 500, 1_000, 2_500, 5_000, 10_000, 20_000, 30_000, 60_000, 120_000];

    public static int Length => UpperBoundsMs.Count + 1;

    public static int[] Empty() => new int[Length];

    public static int BucketOf(int milliseconds)
    {
        for (var i = 0; i < UpperBoundsMs.Count; i++)
        {
            if (milliseconds <= UpperBoundsMs[i])
            {
                return i;
            }
        }

        return UpperBoundsMs.Count;
    }

    /// <summary>A histogram holding one request of <paramref name="milliseconds"/>, or none when the duration is unknown.</summary>
    public static int[] Of(int? milliseconds)
    {
        var histogram = Empty();
        if (milliseconds is { } ms)
        {
            histogram[BucketOf(Math.Max(0, ms))]++;
        }

        return histogram;
    }

    public static int[] Merge(int[] a, int[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var merged = Empty();
        for (var i = 0; i < merged.Length; i++)
        {
            merged[i] = (i < a.Length ? a[i] : 0) + (i < b.Length ? b[i] : 0);
        }

        return merged;
    }

    /// <summary>The estimated <paramref name="quantile"/> (0 to 1) in milliseconds, or null when the histogram is empty.</summary>
    public static int? Percentile(IReadOnlyList<int> histogram, double quantile)
    {
        ArgumentNullException.ThrowIfNull(histogram);
        long total = 0;
        foreach (var count in histogram)
        {
            total += count;
        }

        if (total == 0)
        {
            return null;
        }

        var rank = Math.Max(1, (long)Math.Ceiling(quantile * total));
        long seen = 0;
        for (var i = 0; i < histogram.Count; i++)
        {
            seen += histogram[i];
            if (seen >= rank)
            {
                return UpperBoundsMs[Math.Min(i, UpperBoundsMs.Count - 1)];
            }
        }

        return UpperBoundsMs[^1];
    }
}
