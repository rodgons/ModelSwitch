using ModelSwitch.Usage.Domain;
using Shouldly;

namespace ModelSwitch.UnitTests.Usage;

/// <summary>usage_hourly keeps latency as counts over fixed bucket bounds, so hours can be merged and percentiles estimated (data-model.md).</summary>
public sealed class LatencyHistogramTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 0)]
    [InlineData(51, 1)]
    [InlineData(1000, 4)]
    [InlineData(1001, 5)]
    [InlineData(120_000, 11)]
    [InlineData(int.MaxValue, 12)]
    public void A_duration_falls_in_the_first_bucket_whose_upper_bound_it_does_not_exceed(int milliseconds, int expected)
        => LatencyHistogram.BucketOf(milliseconds).ShouldBe(expected);

    [Fact]
    public void There_is_one_more_bucket_than_bounds_for_everything_above_the_last_bound()
        => LatencyHistogram.Empty().Length.ShouldBe(LatencyHistogram.UpperBoundsMs.Count + 1);

    [Fact]
    public void Recording_counts_one_in_the_right_bucket_and_none_for_an_unknown_duration()
    {
        LatencyHistogram.Of(300)[3].ShouldBe(1);
        LatencyHistogram.Of(300).Sum().ShouldBe(1);
        LatencyHistogram.Of(null).Sum().ShouldBe(0);
    }

    [Fact]
    public void Merging_adds_bucket_by_bucket()
    {
        var merged = LatencyHistogram.Merge(LatencyHistogram.Of(10), LatencyHistogram.Of(10));
        merged[0].ShouldBe(2);
        LatencyHistogram.Merge(merged, LatencyHistogram.Of(700))[4].ShouldBe(1);
    }

    [Fact]
    public void A_percentile_is_the_upper_bound_of_the_bucket_that_holds_it()
    {
        // 90 fast requests (<= 50 ms) and 10 slow ones (<= 2500 ms).
        var histogram = LatencyHistogram.Empty();
        histogram[0] = 90;
        histogram[5] = 10;

        LatencyHistogram.Percentile(histogram, 0.5).ShouldBe(50);
        LatencyHistogram.Percentile(histogram, 0.9).ShouldBe(50);
        LatencyHistogram.Percentile(histogram, 0.95).ShouldBe(2500);
    }

    [Fact]
    public void The_overflow_bucket_reports_the_last_bound_and_an_empty_histogram_has_no_percentile()
    {
        var histogram = LatencyHistogram.Of(500_000);
        LatencyHistogram.Percentile(histogram, 0.5).ShouldBe(LatencyHistogram.UpperBoundsMs[^1]);
        LatencyHistogram.Percentile(LatencyHistogram.Empty(), 0.5).ShouldBeNull();
    }
}
