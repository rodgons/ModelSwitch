using ModelSwitch.Routing.Contracts;

namespace ModelSwitch.Routing.Domain;

/// <summary>
/// The worst-case Estimated Cost of one Candidate (routing-and-resilience.md, "Worst-case cost"): the input estimate and the full output
/// ceiling at the Price in force, nothing cached. The router ranks and caps on it, and the simulation shows it for every priced Candidate.
/// </summary>
internal static class WorstCase
{
    public static decimal Cost(int inputTokens, int maxOutputTokens, PriceInForce price)
    {
        ArgumentNullException.ThrowIfNull(price);
        return (inputTokens * price.InputPerMillion / 1_000_000m) + (maxOutputTokens * price.OutputPerMillion / 1_000_000m);
    }
}
