using ModelSwitch.SharedKernel;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Usage.Domain;

internal sealed class CostCalculator : ICostCalculator
{
    private const decimal _million = 1_000_000m;

    public Money WorstCase(int estimatedInputTokens, int maxOutputTokens, PriceRates price)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedInputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(maxOutputTokens);
        ArgumentNullException.ThrowIfNull(price);

        return Actual(estimatedInputTokens, cachedInputTokens: null, maxOutputTokens, price);
    }

    public Money Actual(int inputTokens, int? cachedInputTokens, int outputTokens, PriceRates price)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(outputTokens);
        ArgumentNullException.ThrowIfNull(price);
        if (cachedInputTokens is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cachedInputTokens), "Cached input tokens cannot be negative.");
        }

        var cached = Math.Min(cachedInputTokens ?? 0, inputTokens);
        var cachedRate = price.CachedInputPerMillion ?? price.InputPerMillion;

        var input = (((decimal)inputTokens - cached) * price.InputPerMillion + (decimal)cached * cachedRate) / _million;
        var output = (decimal)outputTokens * price.OutputPerMillion / _million;

        // Each component is rounded on its own, as the usage_record stores input_cost and output_cost separately.
        return Money.FromUsd(input).Round() + Money.FromUsd(output).Round();
    }

    public int ResolveMaxOutputTokens(int? requested, int? policyDefault)
        => requested is > 0 ? requested.Value
            : policyDefault is > 0 ? policyDefault.Value
            : ICostCalculator.DefaultMaxOutputTokens;

    public Money RequestEstimatedCost(IReadOnlyList<AttemptRecord> attempts, int maxOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        var total = Money.Zero;
        foreach (var attempt in attempts)
        {
            if (AttemptCosting.Cost(attempt, maxOutputTokens, this) is { } draft)
            {
                total += draft.TotalCost;
            }
        }

        return total;
    }
}
