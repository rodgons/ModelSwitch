using ModelSwitch.Identity.Domain;

namespace ModelSwitch.Identity.Application;

/// <summary>Read access to API Keys by Key Prefix, joined with the owning Tenant's status.</summary>
internal interface IApiKeyStore
{
    Task<ApiKeyRecord?> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken);
}

internal sealed record ApiKeyRecord(
    Guid ApiKeyId,
    Guid TenantId,
    string KeyPrefix,
    byte[] KeyHash,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    TenantStatus TenantStatus);
