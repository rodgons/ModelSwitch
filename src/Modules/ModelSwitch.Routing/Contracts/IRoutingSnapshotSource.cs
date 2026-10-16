namespace ModelSwitch.Routing.Contracts;

/// <summary>
/// The catalog and Tenant access this instance routes on right now: the same runtime configuration snapshot the inference path reads.
/// The Configuration module implements it (it depends on Routing, not the reverse), so the routing simulation decides on exactly what a
/// request would see.
/// </summary>
public interface IRoutingSnapshotSource
{
    /// <summary>Candidate models with Capabilities and the Price in force, and the latest Policy Version of each policy.</summary>
    RoutingSnapshot Current { get; }

    /// <summary>The Tenant's allowed aliases and per-request cost limit; every alias and no limit when it has none.</summary>
    TenantModelAccess AccessFor(Guid tenantId);
}
