using ModelSwitch.Configuration.Contracts;
using ModelSwitch.Routing.Contracts;

namespace ModelSwitch.Configuration.Application;

/// <summary>Exposes the current <see cref="ConfigurationSnapshot"/> to the Routing module through its own contract.</summary>
internal sealed class RoutingSnapshotSource(IConfigurationSnapshotProvider snapshots) : IRoutingSnapshotSource
{
    public RoutingSnapshot Current => snapshots.Current.Routing;

    public TenantModelAccess AccessFor(Guid tenantId) => snapshots.Current.AccessFor(tenantId);
}
