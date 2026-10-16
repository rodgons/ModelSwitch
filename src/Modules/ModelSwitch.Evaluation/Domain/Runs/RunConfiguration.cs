using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModelSwitch.Evaluation.Domain.Runs;

/// <summary>The Dataset Version a run executes, with the content hash that proves two runs used the same Cases.</summary>
internal sealed record FrozenDataset(Guid Id, int Version, string ContentSha256, string TaskKind);

/// <summary>
/// What serves the run's candidate calls, resolved at creation (evaluation.md section 4): a Model Alias with the Policy Version that
/// was current then, or one pinned Provider Model (<see cref="ProviderModel"/> is its <c>provider/model</c> name, for display).
/// </summary>
internal sealed record FrozenTarget
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Alias { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? PolicyId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PolicyVersion { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ProviderModelId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderModel { get; init; }

    /// <summary><c>alias@vN</c>, or the pinned model's <c>provider/model</c>.</summary>
    [JsonIgnore]
    public string Label => Alias is not null
        ? $"{Alias}@v{PolicyVersion?.ToString(CultureInfo.InvariantCulture)}"
        : ProviderModel ?? ProviderModelId?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty;

    public static FrozenTarget ForAlias(string alias, Guid policyId, int policyVersion)
        => new() { Alias = alias, PolicyId = policyId, PolicyVersion = policyVersion };

    public static FrozenTarget ForProviderModel(Guid providerModelId, string providerModel)
        => new() { ProviderModelId = providerModelId, ProviderModel = providerModel };
}

/// <summary>The Prompt Version, with its immutable template copied in so the run never reads it again.</summary>
internal sealed record FrozenPrompt(Guid Id, int Version, JsonElement Template);

/// <summary>Generation parameters sent with every candidate call; a null one is not sent (the policy or Provider default applies).</summary>
internal sealed record GenerationParams(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Temperature,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? TopP,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Seed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxOutputTokens);

/// <summary>The <c>llm_judge</c> alias with the Policy Version that was current at creation, frozen like the candidate target.</summary>
internal sealed record FrozenJudge(string Alias, Guid PolicyId, int PolicyVersion);

/// <summary>The Evaluator kind, version and config as written (canonicalized only for hashing).</summary>
internal sealed record FrozenEvaluator(string Kind, string Version, JsonElement Config)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FrozenJudge? Judge { get; init; }
}

/// <summary>
/// The full, frozen Run Configuration of one Evaluation Run (<c>evaluation_run.run_configuration</c>, camelCase JSON), with every
/// reference resolved at creation, plus the reproducibility facts (evaluation.md section 6). <see cref="Sha256"/> hashes everything
/// that decides what the run computes; <see cref="Concurrency"/> and <see cref="GatewayVersion"/> are recorded but not hashed, so the
/// same configuration run with another parallelism or on another build still counts as "the same configuration" for variance.
/// </summary>
internal sealed record RunConfiguration(
    FrozenDataset Dataset,
    FrozenTarget Target,
    FrozenPrompt Prompt,
    GenerationParams Params,
    FrozenEvaluator Evaluator,
    int Concurrency,
    string GatewayVersion)
{
    /// <summary>The members left out of <see cref="Sha256"/>.</summary>
    public static IReadOnlyList<string> UnhashedMembers { get; } = ["concurrency", "gatewayVersion"];

    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, _options);

    public JsonElement ToElement() => JsonSerializer.SerializeToElement(this, _options);

    public static RunConfiguration Parse(string json)
        => JsonSerializer.Deserialize<RunConfiguration>(json, _options) ?? throw new JsonException("The run configuration is empty.");

    /// <summary>Lower-case hex SHA-256 of the canonical JSON of the configuration without <see cref="UnhashedMembers"/>.</summary>
    public string Sha256()
    {
        var node = JsonSerializer.SerializeToNode(this, _options)!.AsObject();
        foreach (var member in UnhashedMembers)
        {
            node.Remove(member);
        }

        using var document = JsonDocument.Parse(node.ToJsonString());
        var canonical = CanonicalJson.Serialize(document.RootElement);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
