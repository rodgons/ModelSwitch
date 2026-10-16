#pragma warning disable CS1573 // Record parameters are documented only where they need explanation.

using ModelSwitch.Registry.Contracts;

namespace ModelSwitch.Routing.Contracts;

public static class CapabilityNames
{
    public const string Streaming = "streaming";
    public const string JsonObject = "jsonObject";
    public const string JsonSchema = "jsonSchema";
}

/// <summary>Capabilities of a Provider Model as declared in the Registry (data-model.md, <c>registry.provider_model.capabilities</c>).</summary>
public sealed record ModelCapabilities(
    bool Streaming,
    bool JsonObject,
    bool JsonSchema,
    int ContextWindow,
    int MaxOutputTokens,
    IReadOnlySet<string> Parameters,
    string? Tokenizer)
{
    /// <summary>Whether the model declares the named Capability: <c>streaming</c>, <c>jsonObject</c>, <c>jsonSchema</c> or a parameter name.</summary>
    public bool Supports(string capability) => capability switch
    {
        CapabilityNames.Streaming => Streaming,
        CapabilityNames.JsonObject => JsonObject,
        CapabilityNames.JsonSchema => JsonSchema,
        _ => Parameters.Contains(capability),
    };
}

/// <summary>The Price in force, in USD per million tokens.</summary>
public sealed record PriceInForce(decimal InputPerMillion, decimal OutputPerMillion, decimal? CachedInputPerMillion = null, Guid? ModelPriceId = null);

/// <summary>A Candidate as the catalog snapshot describes it. Contains no secret.</summary>
public sealed record CandidateModel(
    Guid ProviderModelId,
    Guid ProviderId,
    string ProviderName,
    string ProviderModelIdentifier,
    bool ProviderEnabled,
    bool ModelEnabled,
    ModelCapabilities Capabilities,
    PriceInForce? Price,
    ProviderModelTarget? Target = null);

/// <summary>
/// Immutable configuration the router decides on: alias to policy, Policy Versions (the latest per policy) and the
/// catalog. Built elsewhere (the runtime configuration snapshot), so <see cref="IRouter"/> does no I/O.
/// </summary>
public sealed record RoutingSnapshot(
    IReadOnlyDictionary<string, Guid> AliasToPolicy,
    IReadOnlyDictionary<Guid, PolicySnapshot> Policies,
    IReadOnlyDictionary<Guid, CandidateModel> Models)
{
    /// <summary>
    /// The Model Aliases whose latest Policy Version lists <paramref name="providerModelId"/> as a Candidate (in <c>candidates</c>, any
    /// rule or <c>default</c>), in ordinal order. A pinned Provider Model is allowed for a Tenant only through one of these.
    /// </summary>
    public IReadOnlyList<string> AliasesReaching(Guid providerModelId)
        => [.. AliasToPolicy
            .Where(a => Policies.TryGetValue(a.Value, out var policy) && Lists(policy.Definition, providerModelId))
            .Select(a => a.Key)
            .Order(StringComparer.Ordinal)];

    private static bool Lists(PolicyDefinition definition, Guid providerModelId)
        => definition.Candidates.Contains(providerModelId)
           || definition.Default.Contains(providerModelId)
           || definition.Rules.Any(r => r.Candidates.Contains(providerModelId));
}

/// <summary>Which Model Aliases a Tenant may use and its per-request cost limit (<c>identity.tenant_limits</c>).</summary>
/// <param name="AllowedAliases">Alias names, or <c>*</c> for every alias.</param>
/// <param name="MaxRequestCost">Null: no tenant limit.</param>
public sealed record TenantModelAccess(IReadOnlyCollection<string> AllowedAliases, decimal? MaxRequestCost)
{
    /// <summary>The Tenant may use every alias (<c>*</c>), and therefore every Provider Model when pinned.</summary>
    public bool AllowsEveryAlias => AllowedAliases.Contains("*");

    public bool Allows(string alias) => AllowsEveryAlias || AllowedAliases.Contains(alias);

    /// <summary>
    /// Whether a request pinned to <paramref name="providerModelId"/> may run (evaluation.md section 4): with <c>*</c> any Provider
    /// Model may, otherwise only one that at least one of the Tenant's aliases routes to, so pinning never reaches a model the
    /// Tenant's aliases could not.
    /// </summary>
    public bool AllowsPinned(Guid providerModelId, RoutingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return AllowsEveryAlias || snapshot.AliasesReaching(providerModelId).Any(Allows);
    }
}

/// <summary>
/// Estimated input tokens, counted per tokenizer because each Provider Model names its own. Computed outside the
/// router (usage-and-budgets.md); the router only looks the number up.
/// </summary>
/// <param name="ByTokenizer">Estimate per tokenizer name.</param>
/// <param name="Fallback">Used for a model whose tokenizer is undeclared or missing from <paramref name="ByTokenizer"/>: <c>ceil(utf8Bytes / 3)</c>.</param>
public sealed record InputTokenEstimate(IReadOnlyDictionary<string, int> ByTokenizer, int Fallback)
{
    public static InputTokenEstimate Uniform(int tokens) => new(new Dictionary<string, int>(), tokens);

    public int For(string? tokenizer) => tokenizer is not null && ByTokenizer.TryGetValue(tokenizer, out var tokens) ? tokens : Fallback;
}

/// <param name="Alias">The Model Alias the request asked for. Ignored (only named in messages) when <see cref="PinnedProviderModelId"/> is set.</param>
/// <param name="TaskType">From <c>X-ModelSwitch-Task</c>, if any.</param>
/// <param name="RequiredCapabilities">What the request needs: <c>streaming</c>, <c>jsonObject</c>, <c>jsonSchema</c> and any requested parameter.</param>
/// <param name="MaxOutputTokens">The request's limit; null uses the Policy Version's <c>defaultMaxOutputTokens</c>.</param>
/// <param name="OpenCircuits">Provider Model id to remaining break duration, for each circuit open on this instance. Absent means closed.</param>
public sealed record RoutingInput(
    Guid TenantId,
    string Alias,
    TenantModelAccess Access,
    string? TaskType,
    IReadOnlyCollection<string> RequiredCapabilities,
    InputTokenEstimate EstimatedInputTokens,
    int? MaxOutputTokens,
    RoutingSnapshot Snapshot,
    IReadOnlyDictionary<Guid, TimeSpan> OpenCircuits)
{
    /// <summary>
    /// A Policy Version to route on instead of the latest one the alias resolves to: an Evaluation Run freezes the version current at
    /// its creation (evaluation.md section 4). The alias must still exist and be allowed; the version's own Candidates and settings
    /// apply, checked for Eligibility against the current catalog.
    /// </summary>
    public PolicySnapshot? PinnedPolicy { get; init; }

    /// <summary>
    /// Bypass routing and decide on this one Provider Model (evaluation.md section 4). The Tenant must be allowed it
    /// (<see cref="TenantModelAccess.AllowsPinned"/>); every Eligibility filter except <c>provider_not_allowed</c> applies, with the
    /// Tenant's <c>max_request_cost</c> as the only cost limit.
    /// </summary>
    public Guid? PinnedProviderModelId { get; init; }
}
