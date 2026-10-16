using ModelSwitch.SharedKernel;

namespace ModelSwitch.Usage.Contracts;

/// <summary>A Price's per-million-token rates (USD), as stored in <c>registry.model_price</c>.</summary>
/// <param name="InputPerMillion">Input token rate.</param>
/// <param name="CachedInputPerMillion">Cached Input Token rate; null means no discount (cached tokens cost the input rate).</param>
/// <param name="OutputPerMillion">Output token rate.</param>
public sealed record PriceRates(decimal InputPerMillion, decimal? CachedInputPerMillion, decimal OutputPerMillion);

/// <summary>
/// The only implementation of the cost formulas (usage-and-budgets.md section 5). Pure: no I/O, no clock. Routing uses
/// <see cref="WorstCase"/> for eligibility and cost ranking, Reservations use it as the hold, settlement and Evaluation
/// use <see cref="Actual"/>. Every result is rounded half-to-even at 10 decimal places (the <c>numeric(20,10)</c> scale).
/// </summary>
public interface ICostCalculator
{
    /// <summary>Fallback ceiling when neither the request nor the policy sets one.</summary>
    const int DefaultMaxOutputTokens = 1024;

    /// <summary>
    /// The most a request can cost: <paramref name="estimatedInputTokens"/> all at the full input rate (nothing is assumed
    /// cached, because a cache hit is only known once the Provider answers) plus <paramref name="maxOutputTokens"/> at the
    /// output rate.
    /// </summary>
    Money WorstCase(int estimatedInputTokens, int maxOutputTokens, PriceRates price);

    /// <summary>
    /// The Estimated Cost of Provider-reported (or estimated) usage. <paramref name="cachedInputTokens"/> is a subset of
    /// <paramref name="inputTokens"/> priced at the cached rate; null means not reported, so all input is priced in full.
    /// A cached count above the input count is clamped to it.
    /// </summary>
    Money Actual(int inputTokens, int? cachedInputTokens, int outputTokens, PriceRates price);

    /// <summary>
    /// The output-token ceiling a request is costed and routed with: the client's <c>max_tokens</c> when given, otherwise
    /// the Routing Policy's <c>defaultMaxOutputTokens</c>, otherwise <see cref="DefaultMaxOutputTokens"/>.
    /// </summary>
    int ResolveMaxOutputTokens(int? requested, int? policyDefault);

    /// <summary>
    /// A request's Estimated Cost: the sum of the Usage Records settlement writes for <paramref name="attempts"/> (each priced with
    /// its <see cref="AttemptRecord.Pricing"/>; an ambiguous one at its worst case with <paramref name="maxOutputTokens"/>). It is the
    /// amount <see cref="IUsageLedger.SettleAsync"/> books, so an in-process caller can report it without reading it back.
    /// </summary>
    Money RequestEstimatedCost(IReadOnlyList<AttemptRecord> attempts, int maxOutputTokens);
}
