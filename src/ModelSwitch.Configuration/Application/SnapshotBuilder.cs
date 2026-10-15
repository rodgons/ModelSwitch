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

        var models = catalogSnapshot.Entries.ToDictionary(
            e => e.ProviderModelId,
            e => new CandidateModel(
                e.ProviderModelId,
                e.ProviderId,
                e.ProviderName,
                e.ProviderModelIdentifier,
                e.ProviderEnabled,
                e.ModelEnabled,
                CapabilitiesParser.Parse(e.CapabilitiesJson),
                e.Price is { } price ? new PriceInForce(price.InputPerMillion, price.OutputPerMillion) : null));

        var routing = new RoutingSnapshot(policyCatalog.AliasToPolicy, policyCatalog.Policies, models);
        return new ConfigurationSnapshot(version, time.GetUtcNow(), routing, limits);
    }
}
