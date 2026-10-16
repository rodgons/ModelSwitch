using Microsoft.Extensions.Configuration;
using ModelSwitch.SharedKernel.Retention;
using Shouldly;

namespace ModelSwitch.UnitTests.SharedKernel;

/// <summary>data-model.md "Retention": how long each kind of data is kept.</summary>
public sealed class RetentionOptionsTests
{
    private static RetentionOptions With(string? requestDays)
        => RetentionOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [RetentionOptions.RequestDaysSetting] = requestDays }).Build());

    [Fact]
    public void The_documented_defaults_apply()
    {
        var options = With(null);

        options.RequestDays.ShouldBe(30);
        options.UsageDays.ShouldBe(400);
        options.AuditDays.ShouldBe(400);
        options.OutboxDays.ShouldBe(7);
        options.JobDays.ShouldBe(14);
    }

    [Theory]
    [InlineData("7", 7)]
    [InlineData("400", 400)]
    [InlineData("0", 30)]
    [InlineData("401", 30)]
    [InlineData("-3", 30)]
    [InlineData("a month", 30)]
    public void Request_days_are_configurable_between_1_and_400(string value, int expected)
        => With(value).RequestDays.ShouldBe(expected);

    [Fact]
    public void Each_cutoff_is_its_period_before_now()
    {
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

        var cutoffs = With("10").CutoffsAt(now);

        cutoffs.ShouldBe(new RetentionCutoffs(now.AddDays(-10), now.AddDays(-400), now.AddDays(-400), now.AddDays(-7), now.AddDays(-14)));
    }
}
