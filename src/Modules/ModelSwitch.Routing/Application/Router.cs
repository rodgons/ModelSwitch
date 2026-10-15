using ModelSwitch.Routing.Contracts;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Routing.Application;

/// <summary>
/// The pure routing pipeline (routing-and-resilience.md section 1): resolve the alias, select the candidate set,
/// filter for Eligibility, rank, truncate. It reads only <see cref="RoutingInput"/>.
/// </summary>
internal sealed class Router : IRouter
{
    public RoutingOutcome Decide(RoutingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var snapshot = input.Snapshot;

        if (!snapshot.AliasToPolicy.TryGetValue(input.Alias, out var policyId)
            || !snapshot.Policies.TryGetValue(policyId, out var policy))
        {
            return Fail(null, ErrorCode.ModelNotFound, "The requested model does not exist.");
        }

        if (!input.Access.Allows(input.Alias))
        {
            return Fail(null, ErrorCode.ModelNotAllowed, "The API key's tenant may not use the requested model.");
        }

        var definition = policy.Definition;
        var (candidateIds, ruleMatched) = SelectCandidates(definition, input);
        var maxOutput = input.MaxOutputTokens ?? definition.DefaultMaxOutputTokens;
        var costLimit = MinLimit(definition.MaxRequestCost, input.Access.MaxRequestCost);
        var required = input.RequiredCapabilities.Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();

        var eligible = new List<(int Listed, RankedCandidate Candidate)>();
        var excluded = new List<ExcludedCandidate>();
        for (var listed = 0; listed < candidateIds.Count; listed++)
        {
            var id = candidateIds[listed];
            var verdict = Evaluate(id, snapshot, definition, input, required, maxOutput, costLimit);
            if (verdict.Reason is not null)
            {
                excluded.Add(new ExcludedCandidate(id, verdict.Reason));
            }
            else
            {
                eligible.Add((listed, verdict.Candidate!));
            }
        }

        var ordered = Rank(definition.Strategy, eligible);
        if (definition.MaxCandidates is { } max && ordered.Count > max)
        {
            ordered = ordered.Take(max).ToList();
        }

        var decision = new RoutingDecision(
            policy.PolicyId, policy.Version, definition.Strategy, ruleMatched, maxOutput, ordered, excluded, definition.Execution);

        if (ordered.Count > 0)
        {
            return new RoutingOutcome(decision, null, null);
        }

        if (excluded.Count > 0 && excluded.All(e => e.Reason == EligibilityReason.CircuitOpen))
        {
            var retryAfter = excluded.Min(e => input.OpenCircuits[e.ProviderModelId]);
            return new RoutingOutcome(decision, new Error(ErrorCode.ProviderUnavailable, "Every eligible provider is temporarily unavailable."), retryAfter);
        }

        var reasons = excluded.Select(e => e.Reason).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var message = reasons.Count == 0
            ? "No eligible model can serve this request."
            : $"No eligible model can serve this request ({string.Join(", ", reasons)}).";
        return new RoutingOutcome(decision, new Error(ErrorCode.NoEligibleModel, message), null);
    }

    private static RoutingOutcome Fail(RoutingDecision? decision, ErrorCode code, string message)
        => new(decision, new Error(code, message), null);

    private static (IReadOnlyList<Guid> Ids, int? RuleMatched) SelectCandidates(PolicyDefinition definition, RoutingInput input)
    {
        if (definition.Strategy != PolicyStrategy.Rules)
        {
            return (definition.Candidates, null);
        }

        for (var index = 0; index < definition.Rules.Count; index++)
        {
            if (Matches(definition.Rules[index], input))
            {
                return (definition.Rules[index].Candidates, index);
            }
        }

        return (definition.Default, null);
    }

    private static bool Matches(PolicyRule rule, RoutingInput input)
        => (rule.TaskTypes is null || (input.TaskType is not null && rule.TaskTypes.Contains(input.TaskType, StringComparer.Ordinal)))
           && rule.Requires.All(r => input.RequiredCapabilities.Contains(r, StringComparer.Ordinal));

    private static decimal? MinLimit(decimal? policy, decimal? tenant)
        => policy is null ? tenant : tenant is null ? policy : Math.Min(policy.Value, tenant.Value);

    private readonly record struct Verdict(string? Reason, RankedCandidate? Candidate);

    // The filters run in the documented order; the first failing one is the recorded reason.
    private static Verdict Evaluate(
        Guid id,
        RoutingSnapshot snapshot,
        PolicyDefinition definition,
        RoutingInput input,
        List<string> required,
        int maxOutput,
        decimal? costLimit)
    {
        if (!snapshot.Models.TryGetValue(id, out var model) || !model.ProviderEnabled || !model.ModelEnabled)
        {
            return new Verdict(EligibilityReason.ModelDisabled, null);
        }

        var missing = required.FirstOrDefault(c => !model.Capabilities.Supports(c));
        if (missing is not null)
        {
            return new Verdict(EligibilityReason.CapabilityMissing(missing), null);
        }

        var inputTokens = input.EstimatedInputTokens.For(model.Capabilities.Tokenizer);
        if ((long)inputTokens + maxOutput > model.Capabilities.ContextWindow || maxOutput > model.Capabilities.MaxOutputTokens)
        {
            return new Verdict(EligibilityReason.ContextWindow, null);
        }

        if (definition.AllowedProviders is { } allowed && !allowed.Contains(model.ProviderName))
        {
            return new Verdict(EligibilityReason.ProviderNotAllowed, null);
        }

        if (model.Price is not { } price)
        {
            return new Verdict(EligibilityReason.NoPrice, null);
        }

        var worstCase = (inputTokens * price.InputPerMillion / 1_000_000m) + (maxOutput * price.OutputPerMillion / 1_000_000m);
        if (costLimit is { } limit && worstCase > limit)
        {
            return new Verdict(EligibilityReason.OverRequestCostLimit, null);
        }

        if (input.OpenCircuits.ContainsKey(id))
        {
            return new Verdict(EligibilityReason.CircuitOpen, null);
        }

        return new Verdict(null, new RankedCandidate(model.ProviderModelId, model.ProviderId, model.ProviderName, model.ProviderModelIdentifier, worstCase, model.Target));
    }

    private static List<RankedCandidate> Rank(PolicyStrategy strategy, List<(int Listed, RankedCandidate Candidate)> eligible)
        => strategy == PolicyStrategy.Cost
            ? eligible
                .OrderBy(e => e.Candidate.WorstCaseEstimatedCost)
                .ThenBy(e => e.Listed)
                .ThenBy(e => e.Candidate.ProviderModelId)
                .Select(e => e.Candidate)
                .ToList()
            : eligible.Select(e => e.Candidate).ToList(); // static and rules: listed order (already in that order)
}
