using ModelSwitch.Identity.Contracts;
using ModelSwitch.Identity.Domain;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Identity.Application;

internal sealed record CreateApiKeyRequest(string? Name, IReadOnlyList<string?>? Scopes, DateTimeOffset? ExpiresAt);

internal sealed record ApiKeyView(
    Guid Id,
    Guid TenantId,
    string Name,
    string KeyPrefix,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset CreatedAt,
    Guid? CreatedByAdminId)
{
    public static ApiKeyView From(ApiKey key) => new(
        key.Id,
        key.TenantId,
        key.Name,
        DisplayPrefix(key.KeyPrefix),
        key.Scopes,
        key.ExpiresAt,
        key.RevokedAt,
        key.LastUsedAt,
        key.CreatedAt,
        key.CreatedByAdminId);

    /// <summary>The non-secret leading part of the key, e.g. <c>msk_7f3a9c21</c> (CONTEXT.md, Key Prefix).</summary>
    public static string DisplayPrefix(string keyPrefix) => ApiKeyFormat.Marker + keyPrefix;
}

/// <summary>The creation response: the view plus the raw <c>key</c>, which exists nowhere else and is never shown again.</summary>
internal sealed record CreatedApiKeyView(
    Guid Id,
    Guid TenantId,
    string Name,
    string KeyPrefix,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    string Key)
{
    public static CreatedApiKeyView From(ApiKey key, string plaintext) => new(
        key.Id,
        key.TenantId,
        key.Name,
        ApiKeyView.DisplayPrefix(key.KeyPrefix),
        key.Scopes,
        key.ExpiresAt,
        key.CreatedAt,
        plaintext);
}

/// <summary>
/// API Keys of a Tenant (api.md section 3): list, create (the raw key is returned once) and revoke. Every method checks the Tenant
/// Grant first, so a Tenant outside it, or a key of another Tenant, answers 404.
/// </summary>
internal sealed class ApiKeyAdminService(
    IApiKeyAdminStore keys,
    ITenantAdminStore tenants,
    ICurrentAdministrator current,
    ITenantGrantChecker grants,
    ApiKeyHasher hasher,
    VerifiedKeyCache keyCache,
    IGuidGenerator ids,
    TimeProvider time)
{
    private const int _prefixAttempts = 5;

    public async Task<Result<Page<ApiKeyView>>> ListAsync(Guid tenantId, string? limit, string? cursor, CancellationToken cancellationToken)
    {
        var existing = await RequireTenantAsync(tenantId, cancellationToken);
        if (existing.IsFailure)
        {
            return existing.Error;
        }

        var errors = new Dictionary<string, string[]>();
        var (pageSize, after) = PageCursor.Parse(limit, cursor, errors);
        if (errors.Count > 0)
        {
            return Error.Validation("The API key list query is invalid.", errors);
        }

        var rows = await keys.ListAsync(tenantId, pageSize, after, cancellationToken);
        var page = rows.Take(pageSize).ToList();
        return new Page<ApiKeyView>([.. page.Select(ApiKeyView.From)], rows.Count > pageSize ? PageCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null);
    }

    public async Task<Result<CreatedApiKeyView>> CreateAsync(Guid tenantId, CreateApiKeyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var existing = await RequireTenantAsync(tenantId, cancellationToken);
        if (existing.IsFailure)
        {
            return existing.Error;
        }

        var now = time.GetUtcNow();
        var errors = new Dictionary<string, string[]>();
        var name = TenantRules.ValidateName(request.Name, errors);
        var scopes = TenantRules.ValidateScopes(request.Scopes, errors);
        if (request.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            errors["expiresAt"] = ["The expiry must be in the future."];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The API key is invalid.", errors);
        }

        for (var attempt = 0; attempt < _prefixAttempts; attempt++)
        {
            var generated = ApiKeyFormat.Generate();
            var key = new ApiKey
            {
                Id = ids.NewGuid(),
                TenantId = tenantId,
                Name = name!,
                KeyPrefix = generated.Prefix,
                KeyHash = hasher.Hash(generated.Plaintext),
                Scopes = scopes!,
                ExpiresAt = request.ExpiresAt,
                CreatedAt = now,
                CreatedByAdminId = current.Current?.Id,
            };
            var audit = new AuditEntry(AuditActions.ApiKeyCreated, "api_key", key.Id.ToString())
            {
                TenantId = tenantId,
                After = Summarise(key),
            };
            if (await keys.AddAsync(key, audit, cancellationToken))
            {
                return CreatedApiKeyView.From(key, generated.Plaintext);
            }
        }

        return new Error(ErrorCode.InternalError, "A key could not be generated; try again.");
    }

    public async Task<Result> RevokeAsync(Guid tenantId, Guid keyId, CancellationToken cancellationToken)
    {
        var existing = await RequireTenantAsync(tenantId, cancellationToken);
        if (existing.IsFailure)
        {
            return existing.Error;
        }

        var key = await keys.FindAsync(tenantId, keyId, cancellationToken);
        if (key is null)
        {
            return new Error(ErrorCode.NotFound, "The API key was not found.");
        }

        if (key.RevokedAt is not null)
        {
            return Result.Success();
        }

        var audit = new AuditEntry(AuditActions.ApiKeyRevoked, "api_key", keyId.ToString())
        {
            TenantId = tenantId,
            Before = Summarise(key),
        };
        await keys.RevokeAsync(tenantId, keyId, time.GetUtcNow(), audit, cancellationToken);

        // This instance stops accepting the key at once; the others when their cached entry expires (at most 30 s).
        keyCache.Clear();
        return Result.Success();
    }

    private async Task<Result> RequireTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var access = grants.CheckAccess(tenantId);
        if (access.IsFailure)
        {
            return access;
        }

        return await tenants.FindAsync(tenantId, cancellationToken) is null
            ? new Error(ErrorCode.NotFound, "The tenant was not found.")
            : Result.Success();
    }

    private static Dictionary<string, object?> Summarise(ApiKey key) => new()
    {
        ["name"] = key.Name,
        ["keyPrefix"] = ApiKeyView.DisplayPrefix(key.KeyPrefix),
        ["scopes"] = key.Scopes,
        ["expiresAt"] = key.ExpiresAt,
    };
}
