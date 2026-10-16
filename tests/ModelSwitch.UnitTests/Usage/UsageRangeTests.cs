using ModelSwitch.Usage.Domain;
using Shouldly;

namespace ModelSwitch.UnitTests.Usage;

/// <summary>usage-and-budgets.md section 6: which part of a time range is read from usage_hourly and which from the raw requests.</summary>
public sealed class UsageRangeTests
{
    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void A_range_of_a_day_or_less_is_read_raw_in_full_so_percentiles_are_exact()
    {
        var plan = UsageRange.Plan(At(15, 0, 30), At(16, 0, 30));

        plan.Exact.ShouldBeTrue();
        plan.Raw.ShouldBe([(At(15, 0, 30), At(16, 0, 30))]);
        plan.Hourly.ShouldBeNull();
    }

    [Fact]
    public void A_longer_range_reads_whole_hours_from_the_rollup_and_the_partial_hours_at_its_edges_raw()
    {
        var plan = UsageRange.Plan(At(10, 6, 15), At(15, 12, 40));

        plan.Exact.ShouldBeFalse();
        plan.Hourly.ShouldBe((At(10, 7), At(15, 12)));
        plan.Raw.ShouldBe([(At(10, 6, 15), At(10, 7)), (At(15, 12), At(15, 12, 40))]);
    }

    [Fact]
    public void Edges_that_fall_on_the_hour_need_no_raw_slice()
    {
        var plan = UsageRange.Plan(At(10, 6), At(15, 12));

        plan.Hourly.ShouldBe((At(10, 6), At(15, 12)));
        plan.Raw.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(1, 300)]
    [InlineData(6, 900)]
    [InlineData(24, 3_600)]
    [InlineData(24 * 7, 3_600)]
    [InlineData(24 * 30, 86_400)]
    public void The_series_step_grows_with_the_range(int hours, int expectedSeconds)
        => UsageRange.StepFor(TimeSpan.FromHours(hours)).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));

    [Fact]
    public void A_bucket_start_is_the_range_aligned_to_the_step()
    {
        UsageRange.BucketOf(At(15, 13, 47), TimeSpan.FromMinutes(5)).ShouldBe(At(15, 13, 45));
        UsageRange.BucketOf(At(15, 13, 47), TimeSpan.FromDays(1)).ShouldBe(At(15, 0));
    }
}
