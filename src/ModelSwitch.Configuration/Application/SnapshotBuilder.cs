using ModelSwitch.Configuration.Contracts;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Routing.Contracts;

namespace ModelSwitch.Configuration.Application;

internal interface ISnapshotBuilder
{
    /// <summary>Reads every module's configuration through its Contracts loader and assembles a snapshot stamped <paramref name="version"/>.</summary>
    Task<ConfigurationSnapshot> BuildAsync(long version, CancellationToken cancellationToken);
}

internal sealed class SnapshotBuilder(
    ICatalogSnapshotSource catalog,
    IPolicyCatalogSource policies,
    ITenantLimitsSource tenantLimits,
    TimeProvider time) : ISnapshotBuilder
{
    public async Task<ConfigurationSnapshot> BuildAsync(long version, CancellationToken cancellationToken)
    {
        var catalogSnapshot = await catalog.LoadAsync(cancellationToken);
        var policyCatalog = await policies.LoadAsync(cancellationToken);
        var limits = await tenantLimits.LoadAsync(cancellationToken);

        var models = catalogSnapshot.Entries.ToDictionary(e => e.ProviderModelId, ToCandidate);

        var routing = new RoutingSnapshot(policyCatalog.AliasToPolicy, policyCatalog.Policies, models);
        return new ConfigurationSnapshot(version, time.GetUtcNow(), routing, limits);
    }

    private static CandidateModel ToCandidate(CatalogEntry entry)
    {
        var capabilities = CapabilitiesParser.Parse(entry.CapabilitiesJson);
        return new CandidateModel(
            entry.ProviderModelId,
            entry.ProviderId,
            entry.ProviderName,
            entry.ProviderModelIdentifier,
            entry.ProviderEnabled,
            entry.ModelEnabled,
            capabilities,
            entry.Price is { } price ? new PriceInForce(price.InputPerMillion, price.OutputPerMillion, price.CachedInputPerMillion, price.ModelPriceId) : null,
            // The tokenizer lives in the Capabilities document; the attempt loop needs it to estimate ambiguous and interrupted Attempts.
            entry.Target is { } target ? target with { Tokenizer = capabilities.Tokenizer } : null);
    }
}
