#pragma warning disable CS1573 // Record parameters are documented only where they need explanation.

namespace ModelSwitch.Identity.Contracts;

/// <summary>A Tenant's limits (<c>identity.tenant_limits</c>). A null limit means unlimited.</summary>
/// <param name="AllowedAliases">Model Alias names, or <c>*</c> for every alias.</param>
public sealed record TenantLimits(
    Guid TenantId,
    int? RequestsPerMinute,
    int? MaxConcurrent,
    decimal? MaxRequestCost,
    IReadOnlyCollection<string> AllowedAliases);

/// <summary>Bulk read of every Tenant's limits for the runtime configuration snapshot (architecture.md section 6).</summary>
public interface ITenantLimitsSource
{
    /// <summary>Limits by Tenant id. A Tenant without an entry has no limits and may use every alias.</summary>
    Task<IReadOnlyDictionary<Guid, TenantLimits>> LoadAsync(CancellationToken cancellationToken);
}
