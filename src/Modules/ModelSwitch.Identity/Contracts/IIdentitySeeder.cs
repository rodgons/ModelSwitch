namespace ModelSwitch.Identity.Contracts;

/// <summary>Creates the minimal Tenant and API Key that exist before the admin UI does (roadmap Phase 1 deviation).</summary>
public interface IIdentitySeeder
{
    /// <summary>
    /// Finds or creates the Tenant named <paramref name="tenantName"/>, then always creates a new API Key for it
    /// with every Scope. The plaintext key is in the result and is never stored.
    /// </summary>
    Task<SeededApiKey> SeedAsync(string tenantName, string keyName, CancellationToken cancellationToken);
}

/// <param name="TenantId">The Tenant the key belongs to.</param>
/// <param name="TenantCreated">False when the Tenant already existed.</param>
/// <param name="ApiKeyId">The new key's id.</param>
/// <param name="KeyPrefix">The non-secret Key Prefix.</param>
/// <param name="PlaintextKey">Shown once. Only its HMAC is persisted.</param>
public sealed record SeededApiKey(Guid TenantId, bool TenantCreated, Guid ApiKeyId, string KeyPrefix, string PlaintextKey);
