namespace ModelSwitch.Inference.Application;

internal sealed record ModelAliasInfo(string Id, long Created);

/// <summary>
/// The Model Aliases a Tenant may use (<c>GET /v1/models</c>). Phase 1 stub: every static alias is visible to every
/// Tenant. Phase 3 filters by the Tenant's model access; Provider Model IDs are never exposed.
/// </summary>
internal sealed class ModelListService(InferenceOptions options, TimeProvider time)
{
    // OpenAI's `created` is the model's creation time; an alias has none, so it reports when this process started.
    private readonly long _created = time.GetUtcNow().ToUnixTimeSeconds();

    public IReadOnlyList<ModelAliasInfo> ListFor(Guid tenantId)
        => [.. options.StaticAliases.Select(a => new ModelAliasInfo(a.Alias, _created)).OrderBy(a => a.Id, StringComparer.Ordinal)];
}
