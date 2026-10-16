using ModelSwitch.Identity.Contracts;
using ModelSwitch.Identity.Domain;

namespace ModelSwitch.Identity.Application;

/// <summary>
/// Persistence for Administrators. Every write takes the <see cref="AuditEntry"/> that describes it, so the change and its
/// Audit Record commit in one transaction. Returned Administrators are detached snapshots including their grants.
/// </summary>
internal interface IAdministratorStore
{
    Task<Administrator?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken);

    Task<Administrator?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Administrator>> ListAsync(CancellationToken cancellationToken);

    Task<bool> AnyAsync(CancellationToken cancellationToken);

    /// <summary>True when an active Owner other than <paramref name="exceptId"/> exists.</summary>
    Task<bool> AnyOtherActiveOwnerAsync(Guid exceptId, CancellationToken cancellationToken);

    /// <summary>The ids in <paramref name="tenantIds"/> that are not Tenants.</summary>
    Task<IReadOnlyList<Guid>> FindMissingTenantsAsync(IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken);

    /// <summary>Inserts the Administrator and its grants. False, with nothing written, when the email is already taken.</summary>
    Task<bool> AddAsync(Administrator administrator, AuditEntry audit, CancellationToken cancellationToken);

    /// <summary>Like <see cref="AddAsync"/>, but only while no Administrator exists, serialised across concurrent callers.</summary>
    Task<bool> AddFirstAsync(Administrator administrator, AuditEntry audit, CancellationToken cancellationToken);

    /// <summary>
    /// Writes every field of <paramref name="administrator"/> and replaces its grants. Deactivating also deletes the
    /// Administrator's sessions. <paramref name="audit"/> may be null for pure bookkeeping.
    /// </summary>
    Task SaveAsync(Administrator administrator, AuditEntry? audit, CancellationToken cancellationToken);
}

internal interface ISessionStore
{
    /// <summary>Inserts the session and deletes the Administrator's expired ones.</summary>
    Task AddAsync(AdministratorSession session, DateTimeOffset now, CancellationToken cancellationToken);

    Task<AdministratorSession?> FindAsync(string id, CancellationToken cancellationToken);

    Task UpdateExpiryAsync(string id, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    Task DeleteAsync(string id, CancellationToken cancellationToken);
}

internal interface IAuditStore
{
    /// <summary>Newest first, keyset-paged on <c>(occurred_at, id)</c>. Returns up to <paramref name="query"/>.Limit + 1 rows.</summary>
    Task<IReadOnlyList<AuditRecord>> QueryAsync(AuditQuery query, CancellationToken cancellationToken);
}

internal sealed record AuditQuery(
    int Limit,
    Guid? TenantId,
    string? Action,
    DateTimeOffset? From,
    DateTimeOffset? To,
    (DateTimeOffset OccurredAt, Guid Id)? After);
