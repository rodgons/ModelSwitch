using ModelSwitch.SharedKernel;
using ModelSwitch.Usage.Contracts;
using ModelSwitch.Usage.Domain;
using Shouldly;

namespace ModelSwitch.UnitTests.Usage;

public sealed class CostCalculatorTests
{
    private static readonly PriceRates _gpt = new(InputPerMillion: 0.05m, CachedInputPerMillion: 0.005m, OutputPerMillion: 0.4m);
    private static readonly PriceRates _noCacheDiscount = new(2m, null, 8m);
    private readonly CostCalculator _calculator = new();

    [Fact]
    public void Worst_case_prices_every_input_token_at_the_full_rate_and_the_output_ceiling_at_the_output_rate()
    {
        var cost = _calculator.WorstCase(1_000, 500, _gpt);

        // 1000 * 0.05 / 1e6 + 500 * 0.4 / 1e6
        cost.Amount.ShouldBe(0.00005m + 0.0002m);
        cost.Currency.ShouldBe("USD");
    }

    [Fact]
    public void Worst_case_assumes_nothing_is_cached_even_when_a_cached_rate_exists()
    {
        _calculator.WorstCase(1_000_000, 0, _gpt).Amount.ShouldBe(0.05m);
    }

    [Fact]
    public void Worst_case_of_a_free_model_is_zero()
    {
        _calculator.WorstCase(5_000, 5_000, new PriceRates(0m, 0m, 0m)).ShouldBe(Money.Zero);
    }

    [Fact]
    public void Worst_case_is_the_input_cost_plus_the_output_cost_so_ranking_follows_both_rates()
    {
        var cheapInput = new PriceRates(1m, null, 10m);
        var cheapOutput = new PriceRates(3m, null, 2m);

        // A long prompt with a short answer favours the cheap input rate; the reverse favours the cheap output rate.
        (_calculator.WorstCase(100_000, 100, cheapInput) < _calculator.WorstCase(100_000, 100, cheapOutput)).ShouldBeTrue();
        (_calculator.WorstCase(100, 100_000, cheapOutput) < _calculator.WorstCase(100, 100_000, cheapInput)).ShouldBeTrue();
    }

    [Fact]
    public void Actual_prices_cached_tokens_at_the_cached_rate_and_the_rest_at_the_input_rate()
    {
        var cost = _calculator.Actual(inputTokens: 1_000_000, cachedInputTokens: 600_000, outputTokens: 1_000_000, _gpt);

        // 400k * 0.05 + 600k * 0.005 = 0.02 + 0.003, plus 1M output * 0.4
        cost.Amount.ShouldBe(0.023m + 0.4m);
    }

    [Fact]
    public void Actual_without_a_cached_rate_prices_cached_tokens_at_the_input_rate()
    {
        _calculator.Actual(1_000_000, 900_000, 0, _noCacheDiscount).Amount.ShouldBe(2m);
    }

    [Fact]
    public void Actual_with_a_zero_cached_rate_makes_cached_tokens_free()
    {
        _calculator.Actual(1_000_000, 1_000_000, 0, new PriceRates(2m, 0m, 8m)).Amount.ShouldBe(0m);
    }

    [Fact]
    public void Actual_with_null_cached_count_prices_all_input_in_full()
    {
        _calculator.Actual(1_000_000, null, 0, _gpt).Amount.ShouldBe(0.05m);
    }

    [Fact]
    public void Actual_clamps_a_cached_count_above_the_input_count()
    {
        _calculator.Actual(1_000_000, 5_000_000, 0, _gpt).Amount.ShouldBe(0.005m);
    }

    [Fact]
    public void Actual_never_costs_more_than_the_worst_case_for_the_same_tokens()
    {
        var worst = _calculator.WorstCase(2_345, 678, _gpt);

        (_calculator.Actual(2_345, 2_000, 678, _gpt) <= worst).ShouldBeTrue();
    }

    [Theory]
    [InlineData(1, "0")]            // 0.5e-10 is a tie: rounds to the even digit 0
    [InlineData(3, "0.0000000002")] // 1.5e-10 -> 2e-10
    [InlineData(5, "0.0000000002")] // 2.5e-10 -> 2e-10 (half to even, not half up)
    [InlineData(7, "0.0000000004")] // 3.5e-10 -> 4e-10
    public void Rounds_half_to_even_at_ten_decimal_places(int inputTokens, string expected)
    {
        var price = new PriceRates(0.00005m, null, 0m);

        _calculator.Actual(inputTokens, null, 0, price).Amount.ShouldBe(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Components_are_rounded_separately_before_they_are_added()
    {
        // Each component is 0.5e-10 and rounds to 0; their unrounded sum, 1e-10, would not.
        var price = new PriceRates(0.00005m, null, 0.00005m);

        _calculator.Actual(1, null, 1, price).Amount.ShouldBe(0m);
    }

    [Fact]
    public void Zero_tokens_cost_nothing()
    {
        _calculator.Actual(0, 0, 0, _gpt).Amount.ShouldBe(0m);
        _calculator.WorstCase(0, 0, _gpt).Amount.ShouldBe(0m);
    }

    [Fact]
    public void Negative_token_counts_are_rejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => _calculator.WorstCase(-1, 0, _gpt));
        Should.Throw<ArgumentOutOfRangeException>(() => _calculator.WorstCase(0, -1, _gpt));
        Should.Throw<ArgumentOutOfRangeException>(() => _calculator.Actual(0, -1, 0, _gpt));
    }

    [Fact]
    public void Large_requests_do_not_overflow()
    {
        var price = new PriceRates(75m, null, 150m);

        _calculator.WorstCase(int.MaxValue, int.MaxValue, price).Amount.ShouldBeGreaterThan(0m);
    }

    [Theory]
    [InlineData(256, 1024, 256)] // the client's max_tokens wins
    [InlineData(null, 512, 512)] // else the policy's defaultMaxOutputTokens
    [InlineData(null, null, 1024)] // else the gateway default
    [InlineData(0, 512, 512)] // non-positive is treated as absent
    public void Resolves_the_output_ceiling_from_request_then_policy_then_default(int? requested, int? policyDefault, int expected)
    {
        _calculator.ResolveMaxOutputTokens(requested, policyDefault).ShouldBe(expected);
    }
}
