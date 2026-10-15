using ModelSwitch.Routing.Contracts;

namespace ModelSwitch.UnitTests.Routing;

/// <summary>Builders for router test inputs: a small catalog and a policy over it.</summary>
internal static class RoutingFixtures
{
    public static readonly Guid PolicyId = new("0192f0c8-0000-7000-8000-0000000000a1");
    public static readonly Guid Cheap = new("0192f0c8-0000-7000-8000-000000000001");
    public static readonly Guid Mid = new("0192f0c8-0000-7000-8000-000000000002");
    public static readonly Guid Pricey = new("0192f0c8-0000-7000-8000-000000000003");

    public static ModelCapabilities Capabilities(
        bool streaming = true,
        bool jsonObject = true,
        bool jsonSchema = true,
        int contextWindow = 100_000,
        int maxOutputTokens = 8_000,
        string[]? parameters = null,
        string? tokenizer = null)
        => new(streaming, jsonObject, jsonSchema, contextWindow, maxOutputTokens, (parameters ?? ["temperature", "seed"]).ToHashSet(), tokenizer);

    public static CandidateModel Model(
        Guid id,
        string provider = "p",
        decimal? input = 1m,
        decimal? output = 2m,
        bool providerEnabled = true,
        bool modelEnabled = true,
        ModelCapabilities? capabilities = null)
        => new(
            id,
            ProviderIdOf(provider),
            provider,
            $"model-{id.ToString()[^1]}",
            providerEnabled,
            modelEnabled,
            capabilities ?? Capabilities(),
            input is { } i && output is { } o ? new PriceInForce(i, o) : null);

    public static Guid ProviderIdOf(string provider) => new(provider.GetHashCode(StringComparison.Ordinal), 0, 0, new byte[8]);

    public static PolicyDefinition Definition(
        PolicyStrategy strategy = PolicyStrategy.Static,
        Guid[]? candidates = null,
        PolicyRule[]? rules = null,
        Guid[]? defaults = null,
        string[]? allowedProviders = null,
        decimal? maxRequestCost = null,
        int defaultMaxOutputTokens = 1_000,
        int? maxCandidates = null)
        => new(
            strategy,
            candidates ?? [Cheap, Mid, Pricey],
            rules ?? [],
            defaults ?? [],
            allowedProviders?.ToHashSet(),
            maxRequestCost,
            defaultMaxOutputTokens,
            maxCandidates,
            ExecutionSettings.Default);

    public static RoutingSnapshot Snapshot(PolicyDefinition definition, params CandidateModel[] models)
        => new(
            new Dictionary<string, Guid> { ["balanced"] = PolicyId },
            new Dictionary<Guid, PolicySnapshot> { [PolicyId] = new(PolicyId, "balanced-policy", 7, definition) },
            models.ToDictionary(m => m.ProviderModelId));

    public static RoutingSnapshot Snapshot(PolicyDefinition definition)
        => Snapshot(definition, Model(Cheap, input: 1m, output: 2m), Model(Mid, input: 3m, output: 6m), Model(Pricey, input: 10m, output: 20m));

    public static RoutingInput Input(
        RoutingSnapshot snapshot,
        string alias = "balanced",
        string[]? allowedAliases = null,
        decimal? tenantMaxRequestCost = null,
        string? taskType = null,
        string[]? required = null,
        int inputTokens = 1_000,
        int? maxOutputTokens = 500,
        Dictionary<Guid, TimeSpan>? openCircuits = null)
        => new(
            Guid.Empty,
            alias,
            new TenantModelAccess(allowedAliases ?? ["*"], tenantMaxRequestCost),
            taskType,
            required ?? [],
            InputTokenEstimate.Uniform(inputTokens),
            maxOutputTokens,
            snapshot,
            openCircuits ?? []);
}
