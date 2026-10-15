using Microsoft.EntityFrameworkCore;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Identity.Domain;
using ModelSwitch.SharedKernel;
using Npgsql;

namespace ModelSwitch.Identity.Infrastructure;

internal sealed class IdentitySeeder(
    IDbContextFactory<IdentityDbContext> contexts,
    ApiKeyHasher hasher,
    IGuidGenerator ids,
    TimeProvider time) : IIdentitySeeder
{
    private const int _maxPrefixAttempts = 5;

    public async Task<SeededApiKey> SeedAsync(string tenantName, string keyName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantName);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var now = time.GetUtcNow();

        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Name == tenantName, cancellationToken);
        var created = tenant is null;
        if (tenant is null)
        {
            tenant = new Tenant { Id = ids.NewGuid(), Name = tenantName, CreatedAt = now, UpdatedAt = now };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync(cancellationToken);
        }

        // A prefix collision (40 bits) is astronomically unlikely, but the column is unique, so retry on it.
        for (var attempt = 1; ; attempt++)
        {
            var generated = ApiKeyFormat.Generate();
            var key = new ApiKey
            {
                Id = ids.NewGuid(),
                TenantId = tenant.Id,
                Name = keyName,
                KeyPrefix = generated.Prefix,
                KeyHash = hasher.Hash(generated.Plaintext),
                Scopes = [.. ApiKeyScopes.All],
                CreatedAt = now,
            };
            db.ApiKeys.Add(key);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return new SeededApiKey(tenant.Id, created, key.Id, key.KeyPrefix, generated.Plaintext);
            }
            catch (DbUpdateException ex) when (attempt < _maxPrefixAttempts && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.Entry(key).State = EntityState.Detached;
            }
        }
    }
}
