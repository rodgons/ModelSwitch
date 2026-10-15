using ModelSwitch.Configuration.Contracts;

namespace ModelSwitch.Inference.Application;

internal sealed record ModelAliasInfo(string Id, long Created);

/// <summary>
/// The Model Aliases a Tenant may use (<c>GET /v1/models</c>), read from the configuration snapshot and filtered by the
/// Tenant's model access. Provider Model IDs are never exposed.
/// </summary>
internal sealed class ModelListService(IConfigurationSnapshotProvider configuration, TimeProvider time)
{
    // OpenAI's `created` is the model's creation time; an alias has none, so it reports when this process started.
    private readonly long _created = time.GetUtcNow().ToUnixTimeSeconds();

    public IReadOnlyList<ModelAliasInfo> ListFor(Guid tenantId)
    {
        var snapshot = configuration.Current;
        var access = snapshot.AccessFor(tenantId);
        return [.. snapshot.Routing.AliasToPolicy.Keys
            .Where(access.Allows)
            .Order(StringComparer.Ordinal)
            .Select(alias => new ModelAliasInfo(alias, _created))];
    }
}
