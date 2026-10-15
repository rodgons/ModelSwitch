using ModelSwitch.Identity.Contracts;
using ModelSwitch.Identity.Domain;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Identity.Application;

/// <summary>
/// parse, hash, cache lookup, prefix lookup, constant-time compare, then revocation, expiry and Tenant status
/// (docs/security.md section 2). Every key failure is the same <c>invalid_api_key</c> so callers learn nothing.
/// </summary>
internal sealed class ApiKeyAuthenticator(
    IApiKeyStore store,
    ApiKeyHasher hasher,
    VerifiedKeyCache cache,
    TimeProvider time) : IApiKeyAuthenticator
{
    private static readonly Error _invalidKey = new(ErrorCode.InvalidApiKey, "The API key is missing or invalid.");
    private static readonly Error _tenantSuspended = new(ErrorCode.TenantSuspended, "The tenant is suspended.");

    public async Task<Result<AuthenticatedApiKey>> AuthenticateAsync(string? presentedKey, CancellationToken cancellationToken)
    {
        if (!ApiKeyFormat.TryParse(presentedKey, out var parsed))
        {
            return _invalidKey;
        }

        var presentedHash = hasher.Hash(presentedKey!);
        if (cache.TryGet(presentedHash, out var cached))
        {
            return cached;
        }

        var record = await store.FindByPrefixAsync(parsed.Prefix, cancellationToken);
        if (record is null || !ApiKeyHasher.Matches(record.KeyHash, presentedHash))
        {
            return _invalidKey;
        }

        var now = time.GetUtcNow();
        if (record.RevokedAt is { } revokedAt && revokedAt <= now)
        {
            return _invalidKey;
        }

        if (record.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            return _invalidKey;
        }

        if (record.TenantStatus == TenantStatus.Suspended)
        {
            return _tenantSuspended;
        }

        var authenticated = new AuthenticatedApiKey(record.ApiKeyId, record.TenantId, record.KeyPrefix, record.Scopes);
        cache.Set(presentedHash, authenticated, record.ExpiresAt);
        return authenticated;
    }
}
