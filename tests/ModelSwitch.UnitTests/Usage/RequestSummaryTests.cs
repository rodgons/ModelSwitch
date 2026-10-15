using ModelSwitch.SharedKernel;
using ModelSwitch.Usage.Contracts;
using ModelSwitch.Usage.Domain;
using Shouldly;

namespace ModelSwitch.UnitTests.Usage;

public sealed class RequestSummaryTests
{
    private static readonly DateTimeOffset _t = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    private static AttemptRecord Attempt(
        int number,
        AttemptStatus status = AttemptStatus.Succeeded,
        AttemptKind kind = AttemptKind.Primary,
        UsageSource source = UsageSource.Reported,
        int? input = 10,
        int? output = 5,
        Guid? model = null,
        Money? cost = null)
        => new(number, Guid.Empty, model ?? Guid.CreateVersion7(), kind, status, null, null, null, null, _t, _t, null, input, output, source, cost);

    [Fact]
    public void No_attempts_is_reported_with_no_totals()
    {
        var summary = RequestSummary.From([]);

        (summary.AttemptCount, summary.UsageStatus, summary.TotalInputTokens, summary.SelectedProviderModelId).ShouldBe((0, "reported", null, null));
    }

    [Fact]
    public void Totals_sum_attempts_and_the_successful_one_is_selected()
    {
        var winner = Guid.CreateVersion7();

        var summary = RequestSummary.From(
        [
            Attempt(1, AttemptStatus.Failed, input: 0, output: 0),
            Attempt(2, AttemptStatus.Succeeded, AttemptKind.Fallback, model: winner, input: 7, output: 3),
        ]);

        (summary.AttemptCount, summary.HadFallback, summary.SelectedProviderModelId).ShouldBe((2, true, winner));
        (summary.TotalInputTokens, summary.TotalOutputTokens).ShouldBe((7, 3));
    }

    [Fact]
    public void Null_token_counts_are_skipped_and_all_null_stays_null()
    {
        RequestSummary.From([Attempt(1, input: null, output: null, source: UsageSource.Unavailable)]).TotalInputTokens.ShouldBeNull();
        RequestSummary.From([Attempt(1, input: null, source: UsageSource.Unavailable), Attempt(2, input: 4)]).TotalInputTokens.ShouldBe(4);
    }

    [Fact]
    public void Costs_are_summed_when_present()
    {
        var summary = RequestSummary.From([Attempt(1, cost: Money.FromUsd(0.5m)), Attempt(2, cost: Money.FromUsd(0.25m))]);

        summary.TotalEstimatedCost!.Value.Amount.ShouldBe(0.75m);
        RequestSummary.From([Attempt(1)]).TotalEstimatedCost.ShouldBeNull();
    }

    [Theory]
    [InlineData(UsageSource.Reported, UsageSource.Reported, "reported")]
    [InlineData(UsageSource.Estimated, UsageSource.Estimated, "estimated")]
    [InlineData(UsageSource.Unavailable, UsageSource.Unavailable, "unavailable")]
    [InlineData(UsageSource.Reported, UsageSource.Estimated, "partial")]
    [InlineData(UsageSource.Reported, UsageSource.Unavailable, "partial")]
    [InlineData(UsageSource.Estimated, UsageSource.Unavailable, "unavailable")]
    public void Usage_status_is_the_worst_source_or_partial_when_reported_is_mixed(UsageSource first, UsageSource second, string expected)
        => RequestSummary.From([Attempt(1, source: first), Attempt(2, source: second)]).UsageStatus.ShouldBe(expected);

    [Fact]
    public void Skipped_circuit_open_attempts_take_no_part_in_counts_or_status()
    {
        var summary = RequestSummary.From([Attempt(1, AttemptStatus.SkippedCircuitOpen, source: UsageSource.Unavailable, input: null), Attempt(2)]);

        (summary.AttemptCount, summary.UsageStatus).ShouldBe((1, "reported"));
    }
}
