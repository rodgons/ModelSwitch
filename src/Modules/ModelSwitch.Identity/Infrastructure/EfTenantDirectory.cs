using Microsoft.EntityFrameworkCore;
using ModelSwitch.Identity.Contracts;

namespace ModelSwitch.Identity.Infrastructure;

internal sealed class EfTenantDirectory(IDbContextFactory<IdentityDbContext> contexts) : ITenantDirectory
{
    public async Task<bool> ExistsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId, cancellationToken);
    }
}
