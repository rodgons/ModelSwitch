using ModelSwitch.SharedKernel;
using ModelSwitch.Usage.Contracts;
using ModelSwitch.Usage.Domain;
using Shouldly;

namespace ModelSwitch.UnitTests.Usage;

public sealed class AttemptCostingTests
{
    private static readonly Guid _priceId = Guid.CreateVersion7();
    private static readonly AttemptPricing _pricing = new(_priceId, new PriceRates(2m, 0.5m, 8m));
    private static readonly DateTimeOffset _now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly CostCalculator _calculator = new();

    private static AttemptRecord Attempt(
        AttemptStatus status,
        UsageSource source,
        int? input,
        int? output,
        int? cached = null,
        string? method = null,
        AttemptPricing? pricing = null)
        => new(
            1,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            AttemptKind.Primary,
            status,
            null,
            null,
            null,
            null,
            _now,
            _now,
            null,
            input,
            output,
            source,
            CachedInputTokens: cached,
            EstimateMethod: method,
            Pricing: pricing ?? _pricing);

    [Fact]
    public void Reported_usage_is_priced_with_the_cached_rate_and_keeps_the_price_id()
    {
        var draft = AttemptCosting.Cost(Attempt(AttemptStatus.Succeeded, UsageSource.Reported, 1_000_000, 500_000, cached: 400_000), 1024, _calculator).ShouldNotBeNull();

        // (600000 * 2 + 400000 * 0.5) / 1e6 = 1.4 ; 500000 * 8 / 1e6 = 4
        draft.InputCost.Amount.ShouldBe(1.4m);
        draft.OutputCost.Amount.ShouldBe(4m);
        draft.TotalCost.Amount.ShouldBe(5.4m);
        draft.ModelPriceId.ShouldBe(_priceId);
        draft.CachedInputTokens.ShouldBe(400_000);
        draft.EstimateMethod.ShouldBeNull();
        draft.Source.ShouldBe(UsageSource.Reported);
    }

    [Fact]
    public void Estimated_usage_prices_all_input_in_full_and_records_the_estimate_method()
    {
        var draft = AttemptCosting.Cost(Attempt(AttemptStatus.Succeeded, UsageSource.Estimated, 1_000_000, 0, cached: 999, method: "bytes_div_3"), 1024, _calculator).ShouldNotBeNull();

        draft.InputCost.Amount.ShouldBe(2m);
        draft.CachedInputTokens.ShouldBeNull("cached tokens are only ever reported");
        draft.EstimateMethod.ShouldBe("bytes_div_3");
    }

    [Fact]
    public void An_ambiguous_attempt_is_charged_its_worst_case_as_unavailable_usage()
    {
        var draft = AttemptCosting.Cost(Attempt(AttemptStatus.Ambiguous, UsageSource.Unavailable, 1_000_000, null), 100_000, _calculator).ShouldNotBeNull();

        // 1e6 input at 2 + 1e5 output ceiling at 8 per million
        draft.InputCost.Amount.ShouldBe(2m);
        draft.OutputCost.Amount.ShouldBe(0.8m);
        draft.EstimateMethod.ShouldBe("reservation_upper_bound");
        draft.Source.ShouldBe(UsageSource.Unavailable);
        draft.OutputTokens.ShouldBeNull();
    }

    [Fact]
    public void A_definite_failure_with_zero_tokens_costs_nothing_but_still_has_a_record()
    {
        var draft = AttemptCosting.Cost(Attempt(AttemptStatus.Failed, UsageSource.Reported, 0, 0), 1024, _calculator).ShouldNotBeNull();

        draft.TotalCost.Amount.ShouldBe(0m);
        draft.ModelPriceId.ShouldBe(_priceId);
    }

    [Fact]
    public void An_attempt_skipped_for_an_open_circuit_has_no_usage_record()
    {
        AttemptCosting.Cost(Attempt(AttemptStatus.SkippedCircuitOpen, UsageSource.Reported, null, null), 1024, _calculator).ShouldBeNull();
    }

    [Fact]
    public void Without_a_price_the_record_costs_zero_and_names_no_price()
    {
        var attempt = Attempt(AttemptStatus.Succeeded, UsageSource.Reported, 10, 10) with { Pricing = null };

        var draft = AttemptCosting.Cost(attempt, 1024, _calculator).ShouldNotBeNull();

        draft.TotalCost.ShouldBe(Money.Zero);
        draft.ModelPriceId.ShouldBeNull();
    }

    [Fact]
    public void The_total_is_the_sum_of_the_two_separately_rounded_components()
    {
        // 1 token at 0.3333333333335 per million rounds half-to-even at 10 places on its own.
        var attempt = Attempt(AttemptStatus.Succeeded, UsageSource.Reported, 1, 1, pricing: new AttemptPricing(null, new PriceRates(0.5m, null, 0.5m)));

        var draft = AttemptCosting.Cost(attempt, 1024, _calculator).ShouldNotBeNull();

        draft.InputCost.Amount.ShouldBe(0.0000005m);
        draft.TotalCost.Amount.ShouldBe(0.000001m);
    }
}
