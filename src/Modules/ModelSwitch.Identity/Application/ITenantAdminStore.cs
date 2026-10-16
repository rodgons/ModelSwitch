using ModelSwitch.Identity.Contracts;
using ModelSwitch.Identity.Domain;

namespace ModelSwitch.Identity.Application;

/// <summary>
/// Persistence for the Tenant admin endpoints. Every write takes the <see cref="AuditEntry"/> that describes it and commits
/// the change, the Audit Record and the configuration version bump (architecture.md section 6) in one transaction.
/// Returned entities are detached snapshots.
/// </summary>
internal interface ITenantAdminStore
{
    /// <summary>Newest first, keyset-paged. Returns up to <paramref name="limit"/> + 1 rows; <paramref name="grant"/> filters the Tenants.</summary>
    Task<IReadOnlyList<Tenant>> ListAsync(TenantGrant grant, int limit, (DateTimeOffset CreatedAt, Guid Id)? after, CancellationToken cancellationToken);

    Task<Tenant?> FindAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>The Tenant's limits row, or null when none was ever set (no limits, every alias).</summary>
    Task<TenantLimitsRow?> FindLimitsAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>False, with nothing written, when the name is already taken.</summary>
    Task<bool> AddAsync(Tenant tenant, AuditEntry audit, CancellationToken cancellationToken);

    /// <summary>Writes name, status and <c>updated_at</c>. False, with nothing written, when the new name is already taken.</summary>
    Task<bool> SaveAsync(Tenant tenant, AuditEntry audit, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the rate, concurrency and request-cost limits and sets the monthly Budget (null removes it), keeping the allowed aliases.
    /// </summary>
    Task SaveLimitsAsync(TenantLimitsChange change, AuditEntry audit, CancellationToken cancellationToken);

    /// <summary>Replaces the allowed aliases, keeping the limits.</summary>
    Task SaveAllowedAliasesAsync(Guid tenantId, string[] aliases, DateTimeOffset now, AuditEntry audit, CancellationToken cancellationToken);
}

internal sealed record TenantLimitsChange(
    Guid TenantId,
    int? RequestsPerMinute,
    int? MaxConcurrent,
    decimal? MaxRequestCost,
    decimal? BudgetAmount,
    DateTimeOffset Now);

internal interface IApiKeyAdminStore
{
    /// <summary>Newest first, keyset-paged. Returns up to <paramref name="limit"/> + 1 rows.</summary>
    Task<IReadOnlyList<ApiKey>> ListAsync(Guid tenantId, int limit, (DateTimeOffset CreatedAt, Guid Id)? after, CancellationToken cancellationToken);

    Task<ApiKey?> FindAsync(Guid tenantId, Guid keyId, CancellationToken cancellationToken);

    /// <summary>False, with nothing written, when the Key Prefix is already taken (a random collision; the caller generates another key).</summary>
    Task<bool> AddAsync(ApiKey key, AuditEntry audit, CancellationToken cancellationToken);

    /// <summary>Sets <c>revoked_at</c> on a key that is not yet revoked. False, with nothing written, when it already was.</summary>
    Task<bool> RevokeAsync(Guid tenantId, Guid keyId, DateTimeOffset at, AuditEntry audit, CancellationToken cancellationToken);
}
