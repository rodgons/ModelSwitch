#pragma warning disable CS1573 // Record parameters are documented only where they need explanation.

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
public sealed record PriceInForce(decimal InputPerMillion, decimal OutputPerMillion);

/// <summary>A Candidate as the catalog snapshot describes it. Contains no secret.</summary>
public sealed record CandidateModel(
    Guid ProviderModelId,
    Guid ProviderId,
    string ProviderName,
    string ProviderModelIdentifier,
    bool ProviderEnabled,
    bool ModelEnabled,
    ModelCapabilities Capabilities,
    PriceInForce? Price);

/// <summary>
/// Immutable configuration the router decides on: alias to policy, Policy Versions (the latest per policy) and the
/// catalog. Built elsewhere (the runtime configuration snapshot), so <see cref="IRouter"/> does no I/O.
/// </summary>
public sealed record RoutingSnapshot(
    IReadOnlyDictionary<string, Guid> AliasToPolicy,
    IReadOnlyDictionary<Guid, PolicySnapshot> Policies,
    IReadOnlyDictionary<Guid, CandidateModel> Models);

/// <summary>Which Model Aliases a Tenant may use and its per-request cost limit (<c>identity.tenant_limits</c>).</summary>
/// <param name="AllowedAliases">Alias names, or <c>*</c> for every alias.</param>
/// <param name="MaxRequestCost">Null: no tenant limit.</param>
public sealed record TenantModelAccess(IReadOnlyCollection<string> AllowedAliases, decimal? MaxRequestCost)
{
    public bool Allows(string alias) => AllowedAliases.Contains("*") || AllowedAliases.Contains(alias);
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

/// <param name="Alias">The Model Alias the request asked for.</param>
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
    IReadOnlyDictionary<Guid, TimeSpan> OpenCircuits);
