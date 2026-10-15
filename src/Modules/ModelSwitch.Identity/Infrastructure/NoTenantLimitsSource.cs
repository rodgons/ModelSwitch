using ModelSwitch.Identity.Contracts;

namespace ModelSwitch.Identity.Infrastructure;

/// <summary>
/// Placeholder until <c>identity.tenant_limits</c> exists (Phase 3, Tenant admin): every Tenant is unlimited and may use
/// every alias. Replace with a reader of the table; the snapshot and its consumers need no change.
/// </summary>
internal sealed class NoTenantLimitsSource : ITenantLimitsSource
{
    public Task<IReadOnlyDictionary<Guid, TenantLimits>> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, TenantLimits>>(new Dictionary<Guid, TenantLimits>());
}
