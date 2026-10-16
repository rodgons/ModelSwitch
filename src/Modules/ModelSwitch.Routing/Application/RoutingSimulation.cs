#pragma warning disable CS1573 // Parameters are documented only where they need explanation.

using System.Text.RegularExpressions;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Routing.Contracts;
using ModelSwitch.Routing.Domain;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Routing.Application;

/// <summary>
/// A hypothetical request for <c>POST /admin/routing-policies/{policyId}/simulate</c> (api.md section 3, "Routing"). Nothing is sent to a
/// Provider and nothing is written.
/// </summary>
/// <param name="Version">The Policy Version to route on; null: the latest.</param>
/// <param name="TenantId">Applies the Tenant's per-request cost limit; it must be inside the caller's Tenant Grant.</param>
/// <param name="TaskType">As <c>X-ModelSwitch-Task</c> would carry it.</param>
/// <param name="Requires">Capabilities the request needs: <c>streaming</c>, <c>jsonObject</c>, <c>jsonSchema</c> or a parameter name.</param>
/// <param name="EstimatedInputTokens">The input estimate every Candidate is costed and checked against its context window with.</param>
/// <param name="MaxOutputTokens">The request's output ceiling; null: the policy's <c>defaultMaxOutputTokens</c>.</param>
internal sealed record SimulateRoutingRequest(
    int? Version,
    Guid? TenantId,
    string? TaskType,
    IReadOnlyList<string?>? Requires,
    int? EstimatedInputTokens,
    int? MaxOutputTokens);

/// <summary>A validated <see cref="SimulateRoutingRequest"/>.</summary>
internal sealed record SimulationInput(int? Version, Guid? TenantId, string? TaskType, IReadOnlyList<string> Requires, int EstimatedInputTokens, int? MaxOutputTokens);

/// <summary>
/// One Candidate of the simulated Routing Decision. <c>Status</c> is <c>eligible</c> (ranked, <c>Rank</c> 1 is chosen first),
/// <c>truncated</c> (eligible but ranked after <c>maxCandidates</c>, never tried) or <c>excluded</c> (with its Eligibility reason).
/// </summary>
/// <param name="ProviderName">Null when the Provider Model is no longer in the catalog.</param>
/// <param name="Model">The Provider's own model identifier; null when the Provider Model is no longer in the catalog.</param>
/// <param name="CircuitState">This instance's circuit for the Provider Model: <c>closed</c>, <c>half_open</c> or <c>open</c>.</param>
/// <param name="WorstCaseEstimatedCost">USD at the Price in force; null when the model has no Price.</param>
internal sealed record SimulatedCandidateView(
    Guid ProviderModelId,
    Guid? ProviderId,
    string? ProviderName,
    string? Model,
    string Status,
    int? Rank,
    string? ExclusionReason,
    string CircuitState,
    decimal? WorstCaseEstimatedCost);

/// <summary>
/// The Routing Decision the engine would make. <c>ErrorCode</c> is null when at least one Candidate is eligible, otherwise the error a real
/// request would get (<c>no_eligible_model</c>, <c>provider_unavailable</c> with <c>RetryAfterMs</c>, <c>request_cost_limit_exceeded</c>).
/// </summary>
internal sealed record RoutingSimulationView(
    Guid PolicyId,
    string PolicyName,
    int Version,
    string Strategy,
    int? RuleMatched,
    int EffectiveMaxOutputTokens,
    string? ErrorCode,
    string? Message,
    int? RetryAfterMs,
    IReadOnlyList<SimulatedCandidateView> Candidates,
    string Currency);

/// <summary>
/// The simulation's rules. The decision itself is <see cref="IRouter.Decide"/>, the same pure function a request runs through, on the
/// catalog and circuits of this instance; this class only builds its input and lays the decision out for an Administrator.
/// </summary>
internal static partial class RoutingSimulation
{
    public const int MaxTokens = 10_000_000;
    public const int MaxRequires = 32;

    // The router resolves an alias to a policy; the simulation routes a policy directly, so it binds it to an alias no client can use.
    private const string _alias = " simulation ";
    private static readonly string[] _everyAlias = ["*"];

    public static Result<SimulationInput> Validate(SimulateRoutingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        if (request.Version is < 1)
        {
            errors["version"] = ["The version must be 1 or more; leave it out for the latest."];
        }

        if (request.EstimatedInputTokens is null)
        {
            errors["estimatedInputTokens"] = ["The input token estimate is required."];
        }
        else if (request.EstimatedInputTokens is < 0 or > MaxTokens)
        {
            errors["estimatedInputTokens"] = [$"The input token estimate must be 0 to {MaxTokens:N0}."];
        }

        if (request.MaxOutputTokens is < 1 or > MaxTokens)
        {
            errors["maxOutputTokens"] = [$"The output ceiling must be 1 to {MaxTokens:N0}; leave it out for the policy default."];
        }

        if (request.TaskType is not null && !TaskTypePattern().IsMatch(request.TaskType))
        {
            errors["taskType"] = ["The Task Type must match [a-z0-9_-]{1,64}, as X-ModelSwitch-Task does."];
        }

        var requires = request.Requires ?? [];
        if (requires.Count > MaxRequires || requires.Any(r => r is null || !CapabilityPattern().IsMatch(r)))
        {
            errors["requires"] = [$"At most {MaxRequires} Capability names (streaming, jsonObject, jsonSchema or a parameter name), each [A-Za-z0-9_.-]{{1,64}}."];
        }

        return errors.Count > 0
            ? Error.Validation("The routing simulation is invalid.", errors)
            : new SimulationInput(
                request.Version,
                request.TenantId,
                request.TaskType,
                [.. requires.Select(r => r!).Distinct(StringComparer.Ordinal)],
                request.EstimatedInputTokens!.Value,
                request.MaxOutputTokens);
    }

    /// <param name="catalog">Supplies the candidate models; its policies are ignored in favour of <paramref name="policy"/>.</param>
    /// <param name="access">The Tenant's access, for its per-request cost limit; null: no Tenant.</param>
    public static RoutingSimulationView Run(
        IRouter router,
        PolicySnapshot policy,
        SimulationInput input,
        RoutingSnapshot catalog,
        TenantModelAccess? access,
        IReadOnlyDictionary<Guid, TimeSpan> openCircuits,
        Func<Guid, CircuitState> stateOf)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(stateOf);

        var snapshot = new RoutingSnapshot(
            new Dictionary<string, Guid>(StringComparer.Ordinal) { [_alias] = policy.PolicyId },
            new Dictionary<Guid, PolicySnapshot> { [policy.PolicyId] = policy },
            catalog.Models);
        var outcome = router.Decide(new RoutingInput(
            input.TenantId ?? Guid.Empty,
            _alias,
            new TenantModelAccess(_everyAlias, access?.MaxRequestCost),
            input.TaskType,
            input.Requires,
            InputTokenEstimate.Uniform(input.EstimatedInputTokens),
            input.MaxOutputTokens,
            snapshot,
            openCircuits));

        // The alias is bound and every alias is allowed, so the policy always resolves and the decision is always present.
        var decision = outcome.Decision ?? throw new InvalidOperationException("The router returned no decision for a resolved policy.");
        var candidates = new List<SimulatedCandidateView>();
        SimulatedCandidateView View(Guid id, string status, int? rank, string? reason, decimal? cost)
        {
            catalog.Models.TryGetValue(id, out var model);
            return new SimulatedCandidateView(id, model?.ProviderId, model?.ProviderName, model?.ProviderModelIdentifier, status, rank, reason, Wire(stateOf(id)), cost);
        }

        for (var i = 0; i < decision.Ordered.Count; i++)
        {
            candidates.Add(View(decision.Ordered[i].ProviderModelId, "eligible", i + 1, null, decision.Ordered[i].WorstCaseEstimatedCost));
        }

        foreach (var truncated in decision.Truncated ?? [])
        {
            candidates.Add(View(truncated.ProviderModelId, "truncated", null, null, truncated.WorstCaseEstimatedCost));
        }

        foreach (var excluded in decision.Excluded)
        {
            decimal? cost = catalog.Models.TryGetValue(excluded.ProviderModelId, out var model) && model.Price is { } price
                ? WorstCase.Cost(input.EstimatedInputTokens, decision.EffectiveMaxOutputTokens, price)
                : null;
            candidates.Add(View(excluded.ProviderModelId, "excluded", null, excluded.Reason, cost));
        }

        return new RoutingSimulationView(
            policy.PolicyId,
            policy.Name,
            policy.Version,
            decision.Strategy.ToString().ToLowerInvariant(),
            decision.RuleMatched,
            decision.EffectiveMaxOutputTokens,
            outcome.Error?.Code.Value,
            outcome.Error?.Message,
            outcome.RetryAfter is { } retry ? (int)Math.Ceiling(retry.TotalMilliseconds) : null,
            candidates,
            Money.Usd);
    }

    private static string Wire(CircuitState state) => state switch
    {
        CircuitState.Open => "open",
        CircuitState.HalfOpen => "half_open",
        _ => "closed",
    };

    [GeneratedRegex("^[a-z0-9_-]{1,64}$")]
    private static partial Regex TaskTypePattern();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$")]
    private static partial Regex CapabilityPattern();
}
