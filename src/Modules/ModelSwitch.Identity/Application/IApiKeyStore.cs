using ModelSwitch.Identity.Domain;

namespace ModelSwitch.Identity.Application;

/// <summary>Read access to API Keys by Key Prefix, joined with the owning Tenant's status.</summary>
internal interface IApiKeyStore
{
    Task<ApiKeyRecord?> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken);

    /// <summary>
    /// Sets <c>last_used_at</c> to <paramref name="at"/> unless it was already set within the last
    /// <see cref="ApiKeyUsageRecorder.Interval"/> (so several instances still write at most once a minute per key).
    /// </summary>
    Task MarkUsedAsync(Guid apiKeyId, DateTimeOffset at, CancellationToken cancellationToken);
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
