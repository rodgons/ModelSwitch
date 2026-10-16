using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ModelSwitch.Identity.Application;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Identity.Domain;
using Npgsql;

namespace ModelSwitch.Identity.Infrastructure;

internal sealed class EfAdministratorStore(IDbContextFactory<IdentityDbContext> contexts, IAuditWriter audit) : IAdministratorStore
{
    private const string _bootstrapLock = "SELECT pg_advisory_xact_lock(hashtext('modelswitch.identity.bootstrap'))";

    public async Task<Administrator?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Administrators.AsNoTracking().Include(a => a.Grants).SingleOrDefaultAsync(a => a.Email == normalizedEmail, cancellationToken);
    }

    public async Task<Administrator?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Administrators.AsNoTracking().Include(a => a.Grants).SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Administrator>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Administrators.AsNoTracking().Include(a => a.Grants).OrderBy(a => a.Email).ToListAsync(cancellationToken);
    }

    public async Task<bool> AnyAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Administrators.AnyAsync(cancellationToken);
    }

    public async Task<bool> AnyOtherActiveOwnerAsync(Guid exceptId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Administrators.AnyAsync(a => a.Id != exceptId && a.Active && a.Role == AdministratorRole.Owner, cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> FindMissingTenantsAsync(IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var existing = await db.Tenants.Where(t => tenantIds.Contains(t.Id)).Select(t => t.Id).ToListAsync(cancellationToken);
        return [.. tenantIds.Except(existing).Order()];
    }

    public async Task<bool> AddAsync(Administrator administrator, AuditEntry entry, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await InsertAsync(db, administrator, entry, serialiseFirst: false, cancellationToken);
    }

    public async Task<bool> AddFirstAsync(Administrator administrator, AuditEntry entry, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await InsertAsync(db, administrator, entry, serialiseFirst: true, cancellationToken);
    }

    public async Task SaveAsync(Administrator administrator, AuditEntry? entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var stored = await db.Administrators.Include(a => a.Grants).SingleAsync(a => a.Id == administrator.Id, cancellationToken);
        stored.Email = administrator.Email;
        stored.PasswordHash = administrator.PasswordHash;
        stored.Role = administrator.Role;
        stored.AllTenants = administrator.AllTenants;
        stored.Active = administrator.Active;
        stored.FailedSignInCount = administrator.FailedSignInCount;
        stored.FailedSignInWindowStartedAt = administrator.FailedSignInWindowStartedAt;
        stored.LockedUntil = administrator.LockedUntil;
        stored.LastSignedInAt = administrator.LastSignedInAt;
        stored.UpdatedAt = administrator.UpdatedAt;

        var wanted = administrator.Grants.Select(g => g.TenantId).ToHashSet();
        foreach (var grant in stored.Grants.Where(g => !wanted.Contains(g.TenantId)).ToList())
        {
            stored.Grants.Remove(grant);
        }

        var have = stored.Grants.Select(g => g.TenantId).ToHashSet();
        foreach (var tenantId in wanted.Where(t => !have.Contains(t)))
        {
            stored.Grants.Add(new AdministratorTenantGrant { AdministratorId = stored.Id, TenantId = tenantId });
        }

        await db.SaveChangesAsync(cancellationToken);
        if (!stored.Active)
        {
            await db.AdministratorSessions.Where(s => s.AdministratorId == stored.Id).ExecuteDeleteAsync(cancellationToken);
        }

        if (entry is not null)
        {
            await audit.WriteAsync(entry, transaction.GetDbTransaction(), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<bool> InsertAsync(IdentityDbContext db, Administrator administrator, AuditEntry entry, bool serialiseFirst, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (serialiseFirst)
        {
            await db.Database.ExecuteSqlRawAsync(_bootstrapLock, cancellationToken);
            if (await db.Administrators.AnyAsync(cancellationToken))
            {
                return false;
            }
        }

        db.Administrators.Add(administrator);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_administrator_email" })
        {
            return false;
        }

        await audit.WriteAsync(entry, transaction.GetDbTransaction(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}

internal sealed class EfSessionStore(IDbContextFactory<IdentityDbContext> contexts) : ISessionStore
{
    public async Task AddAsync(AdministratorSession session, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.AdministratorSessions
            .Where(s => s.AdministratorId == session.AdministratorId && (s.ExpiresAt <= now || s.AbsoluteExpiresAt <= now))
            .ExecuteDeleteAsync(cancellationToken);
        db.AdministratorSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AdministratorSession?> FindAsync(string id, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.AdministratorSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    public async Task UpdateExpiryAsync(string id, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.AdministratorSessions.Where(s => s.Id == id).ExecuteUpdateAsync(set => set.SetProperty(s => s.ExpiresAt, expiresAt), cancellationToken);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.AdministratorSessions.Where(s => s.Id == id).ExecuteDeleteAsync(cancellationToken);
    }
}

internal sealed class EfAuditStore(IDbContextFactory<IdentityDbContext> contexts) : IAuditStore
{
    public async Task<IReadOnlyList<AuditRecord>> QueryAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var records = db.AuditRecords.AsNoTracking().AsQueryable();
        if (query.TenantId is { } tenantId)
        {
            records = records.Where(r => r.TenantId == tenantId);
        }

        if (query.Action is not null)
        {
            records = records.Where(r => r.Action == query.Action);
        }

        if (query.From is { } from)
        {
            records = records.Where(r => r.OccurredAt >= from);
        }

        if (query.To is { } to)
        {
            records = records.Where(r => r.OccurredAt < to);
        }

        if (query.After is { } after)
        {
            var (occurredAt, id) = after;
            records = records.Where(r => r.OccurredAt < occurredAt || (r.OccurredAt == occurredAt && r.Id.CompareTo(id) < 0));
        }

        return await records.OrderByDescending(r => r.OccurredAt).ThenByDescending(r => r.Id).Take(query.Limit + 1).ToListAsync(cancellationToken);
    }
}
