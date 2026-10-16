using ModelSwitch.SharedKernel;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Usage.Domain;

/// <summary>The cost figures of one Attempt, as its Usage Record stores them (data-model.md, <c>usage_record</c>).</summary>
internal sealed record UsageRecordDraft(
    Guid? ModelPriceId,
    int? InputTokens,
    int? CachedInputTokens,
    int? OutputTokens,
    UsageSource Source,
    string? EstimateMethod,
    Money InputCost,
    Money OutputCost)
{
    public Money TotalCost => InputCost + OutputCost;
}

/// <summary>
/// Turns an Attempt into its Usage Record (usage-and-budgets.md sections 2, 4 and 5). Pure: the rules live here and the ledger only stores them.
/// </summary>
internal static class AttemptCosting
{
    public const string ReservationUpperBound = "reservation_upper_bound";

    /// <summary>
    /// Null for an Attempt skipped because its circuit was open: it never reached a Provider, so it has no usage and no cost.
    /// Reported usage is priced as reported (cached tokens at the cached rate). Estimated usage is priced with nothing cached.
    /// Unavailable usage (an ambiguous Attempt) is charged its worst case, because it may have been billed.
    /// </summary>
    public static UsageRecordDraft? Cost(AttemptRecord attempt, int maxOutputTokens, ICostCalculator calculator)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(calculator);

        if (attempt.Status == AttemptStatus.SkippedCircuitOpen)
        {
            return null;
        }

        var priceId = attempt.Pricing?.ModelPriceId;
        var input = attempt.InputTokens;
        var output = attempt.OutputTokens;

        if (attempt.Pricing is not { } pricing)
        {
            return new UsageRecordDraft(priceId, input, null, output, attempt.UsageSource, MethodOf(attempt), Money.Zero, Money.Zero);
        }

        var rates = pricing.Rates;
        switch (attempt.UsageSource)
        {
            case UsageSource.Reported:
                {
                    var cached = attempt.CachedInputTokens;
                    return new UsageRecordDraft(
                        priceId,
                        input,
                        cached,
                        output,
                        UsageSource.Reported,
                        null,
                        calculator.Actual(input ?? 0, cached, 0, rates),
                        calculator.Actual(0, null, output ?? 0, rates));
                }

            case UsageSource.Estimated:
                return new UsageRecordDraft(
                    priceId,
                    input,
                    null,
                    output,
                    UsageSource.Estimated,
                    attempt.EstimateMethod,
                    calculator.Actual(input ?? 0, null, 0, rates),
                    calculator.Actual(0, null, output ?? 0, rates));

            default:
                return new UsageRecordDraft(
                    priceId,
                    input,
                    null,
                    output,
                    UsageSource.Unavailable,
                    ReservationUpperBound,
                    calculator.WorstCase(input ?? 0, 0, rates),
                    calculator.WorstCase(0, maxOutputTokens, rates));
        }
    }

    private static string? MethodOf(AttemptRecord attempt) => attempt.UsageSource switch
    {
        UsageSource.Reported => null,
        UsageSource.Estimated => attempt.EstimateMethod,
        _ => ReservationUpperBound,
    };
}
