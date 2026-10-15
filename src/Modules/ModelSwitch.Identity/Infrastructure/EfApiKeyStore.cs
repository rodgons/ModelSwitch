using Microsoft.EntityFrameworkCore;
using ModelSwitch.Identity.Application;

namespace ModelSwitch.Identity.Infrastructure;

internal sealed class EfApiKeyStore(IDbContextFactory<IdentityDbContext> contexts) : IApiKeyStore
{
    public async Task<ApiKeyRecord?> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await (
            from key in db.ApiKeys.AsNoTracking()
            join tenant in db.Tenants.AsNoTracking() on key.TenantId equals tenant.Id
            where key.KeyPrefix == keyPrefix
            select new { key, tenant.Status })
            .SingleOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new ApiKeyRecord(
                row.key.Id,
                row.key.TenantId,
                row.key.KeyPrefix,
                row.key.KeyHash,
                row.key.Scopes,
                row.key.ExpiresAt,
                row.key.RevokedAt,
                row.Status);
    }
}
