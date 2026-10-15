#pragma warning disable CS1573 // Record parameters are documented only where they need explanation.

using ModelSwitch.Registry.Contracts;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Routing.Contracts;

/// <summary>The closed set of Eligibility exclusion reasons (routing-and-resilience.md section 1).</summary>
public static class EligibilityReason
{
    public const string ModelDisabled = "model_disabled";
    public const string CapabilityMissingPrefix = "capability_missing:";
    public const string ContextWindow = "context_window";
    public const string ProviderNotAllowed = "provider_not_allowed";
    public const string NoPrice = "no_price";
    public const string OverRequestCostLimit = "over_request_cost_limit";
    public const string CircuitOpen = "circuit_open";

    public static string CapabilityMissing(string capability) => CapabilityMissingPrefix + capability;
}

/// <summary>An eligible Candidate in rank order.</summary>
/// <param name="WorstCaseEstimatedCost">Input estimate and the maximum output at the Price in force (routing-and-resilience.md, "Worst-case cost").</param>
/// <param name="Target">How to call the model, from the same snapshot the decision was made on, so the attempt loop does no database read.</param>
public sealed record RankedCandidate(
    Guid ProviderModelId,
    Guid ProviderId,
    string ProviderName,
    string ProviderModelIdentifier,
    decimal WorstCaseEstimatedCost,
    ProviderModelTarget? Target = null);

public sealed record ExcludedCandidate(Guid ProviderModelId, string Reason);

/// <summary>The recorded outcome of routing (CONTEXT.md, Routing Decision).</summary>
/// <param name="RuleMatched">Index into the policy's <c>rules</c>; null when no rule matched or the strategy is not <c>rules</c>.</param>
/// <param name="EffectiveMaxOutputTokens">The request's limit, or the policy default.</param>
public sealed record RoutingDecision(
    Guid PolicyId,
    int PolicyVersion,
    PolicyStrategy Strategy,
    int? RuleMatched,
    int EffectiveMaxOutputTokens,
    IReadOnlyList<RankedCandidate> Ordered,
    IReadOnlyList<ExcludedCandidate> Excluded,
    ExecutionSettings Execution);

/// <summary>
/// Either a decision with at least one Candidate, or an error (<c>model_not_found</c>, <c>model_not_allowed</c>,
/// <c>no_eligible_model</c>, <c>provider_unavailable</c>). When the policy was resolved, <see cref="Decision"/> is set
/// even on failure so the exclusions can be recorded. <see cref="RetryAfter"/> accompanies <c>provider_unavailable</c>.
/// </summary>
public sealed record RoutingOutcome(RoutingDecision? Decision, Error? Error, TimeSpan? RetryAfter)
{
    public bool IsSuccess => Error is null;
}

public interface IRouter
{
    /// <summary>A pure function of its input: no I/O, no clock, no randomness.</summary>
    RoutingOutcome Decide(RoutingInput input);
}

public interface IPolicyStore
{
    /// <summary>The latest Policy Version of the policy the Model Alias points to, or null when it is unknown.</summary>
    Task<PolicySnapshot?> GetActiveForAliasAsync(string modelAlias, CancellationToken cancellationToken);
}
