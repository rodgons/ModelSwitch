using Microsoft.EntityFrameworkCore;
using ModelSwitch.Identity.Contracts;

namespace ModelSwitch.Identity.Infrastructure;

/// <summary>Reads every Tenant's limits from <c>identity.tenant_limits</c> for the configuration snapshot (architecture.md section 6).</summary>
internal sealed class EfTenantLimitsSource(IDbContextFactory<IdentityDbContext> contexts) : ITenantLimitsSource
{
    public async Task<IReadOnlyDictionary<Guid, TenantLimits>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.TenantLimits.AsNoTracking().ToListAsync(cancellationToken);
        return rows.ToDictionary(
            row => row.TenantId,
            row => new TenantLimits(row.TenantId, row.RequestsPerMinute, row.MaxConcurrent, row.MaxRequestCost, row.AllowedAliases));
    }
}
