#pragma warning disable CS1573 // Record parameters are documented only where they need explanation.

using ModelSwitch.Identity.Contracts;
using ModelSwitch.Routing.Contracts;

namespace ModelSwitch.Configuration.Contracts;

/// <summary>
/// The immutable runtime configuration one api instance serves from (architecture.md section 6): the catalog and the
/// policies as the router needs them, plus Tenant limits. A request reads <see cref="IConfigurationSnapshotProvider.Current"/>
/// once and keeps that instance, so it never mixes two versions.
/// </summary>
/// <param name="Version">The <c>config.config_version</c> the snapshot was built at.</param>
/// <param name="Routing">Alias to policy, latest Policy Versions and candidate models (with capabilities and current price).</param>
/// <param name="TenantLimits">By Tenant id. A Tenant without an entry is unlimited.</param>
public sealed record ConfigurationSnapshot(
    long Version,
    DateTimeOffset LoadedAt,
    RoutingSnapshot Routing,
    IReadOnlyDictionary<Guid, TenantLimits> TenantLimits)
{
    private static readonly string[] _everyAlias = ["*"];

    /// <summary>Which aliases the Tenant may use and its per-request cost limit, for <see cref="RoutingInput"/>.</summary>
    public TenantModelAccess AccessFor(Guid tenantId) =>
        TenantLimits.TryGetValue(tenantId, out var limits)
            ? new TenantModelAccess(limits.AllowedAliases, limits.MaxRequestCost)
            : new TenantModelAccess(_everyAlias, MaxRequestCost: null);
}

/// <summary>Holder of the current <see cref="ConfigurationSnapshot"/> of this instance.</summary>
public interface IConfigurationSnapshotProvider
{
    /// <summary>True once the first snapshot has been built. <c>GET /ready</c> reports it.</summary>
    bool IsLoaded { get; }

    /// <summary>The current snapshot. Throws <see cref="InvalidOperationException"/> before the first load.</summary>
    ConfigurationSnapshot Current { get; }
}
