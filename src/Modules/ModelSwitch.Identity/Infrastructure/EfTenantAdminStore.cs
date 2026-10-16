using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ModelSwitch.Identity.Application;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Identity.Domain;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Configuration;
using ModelSwitch.Usage.Contracts;
using Npgsql;

namespace ModelSwitch.Identity.Infrastructure;

/// <summary>
/// Every write is one transaction: the change, its Audit Record and the configuration version bump with <c>NOTIFY</c>
/// (architecture.md section 6), so an instance never rebuilds its snapshot from a half-written change.
/// </summary>
internal static class AdminWrite
{
    public static async Task CommitAsync(IdentityDbContext db, IDbContextTransaction transaction, IAuditWriter audit, AuditEntry entry, CancellationToken cancellationToken)
    {
        var dbTransaction = transaction.GetDbTransaction();
        await audit.WriteAsync(entry, dbTransaction, cancellationToken);
        await ConfigVersion.BumpAsync(db.Database.GetDbConnection(), dbTransaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

internal sealed class EfTenantAdminStore(IDbContextFactory<IdentityDbContext> contexts, IAuditWriter audit, IBudgets budgets) : ITenantAdminStore
{
    public async Task<IReadOnlyList<Tenant>> ListAsync(TenantGrant grant, int limit, (DateTimeOffset CreatedAt, Guid Id)? after, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var tenants = db.Tenants.AsNoTracking().AsQueryable();
        if (!grant.IsAll)
        {
            var granted = grant.TenantIds.ToList();
            tenants = tenants.Where(t => granted.Contains(t.Id));
        }

        if (after is { } cursor)
        {
            tenants = tenants.Where(t => t.CreatedAt < cursor.CreatedAt || (t.CreatedAt == cursor.CreatedAt && t.Id.CompareTo(cursor.Id) < 0));
        }

        return await tenants.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Take(limit + 1).ToListAsync(cancellationToken);
    }

    public async Task<Tenant?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
    }

    public async Task<TenantLimitsRow?> FindLimitsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.TenantLimits.AsNoTracking().SingleOrDefaultAsync(l => l.TenantId == tenantId, cancellationToken);
    }

    public async Task<bool> AddAsync(Tenant tenant, AuditEntry entry, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Tenants.Add(tenant);
        if (!await TrySaveAsync(db, cancellationToken))
        {
            return false;
        }

        await AdminWrite.CommitAsync(db, transaction, audit, entry, cancellationToken);
        return true;
    }

    public async Task<bool> SaveAsync(Tenant tenant, AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var stored = await db.Tenants.SingleAsync(t => t.Id == tenant.Id, cancellationToken);
        stored.Name = tenant.Name;
        stored.Status = tenant.Status;
        stored.UpdatedAt = tenant.UpdatedAt;
        if (!await TrySaveAsync(db, cancellationToken))
        {
            return false;
        }

        await AdminWrite.CommitAsync(db, transaction, audit, entry, cancellationToken);
        return true;
    }

    public async Task SaveLimitsAsync(TenantLimitsChange change, AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var row = await EnsureRowAsync(db, change.TenantId, change.Now, cancellationToken);
        row.RequestsPerMinute = change.RequestsPerMinute;
        row.MaxConcurrent = change.MaxConcurrent;
        row.MaxRequestCost = change.MaxRequestCost;
        row.UpdatedAt = change.Now;
        await db.SaveChangesAsync(cancellationToken);

        // The Budget lives in the usage schema (the Reservation UPDATE consults it); it joins this transaction.
        await budgets.SetMonthlyLimitAsync(
            change.TenantId,
            change.BudgetAmount is { } amount ? Money.FromUsd(amount) : null,
            transaction.GetDbTransaction(),
            cancellationToken);
        await AdminWrite.CommitAsync(db, transaction, audit, entry, cancellationToken);
    }

    public async Task SaveAllowedAliasesAsync(Guid tenantId, string[] aliases, DateTimeOffset now, AuditEntry entry, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var row = await EnsureRowAsync(db, tenantId, now, cancellationToken);
        row.AllowedAliases = aliases;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await AdminWrite.CommitAsync(db, transaction, audit, entry, cancellationToken);
    }

    private static async Task<TenantLimitsRow> EnsureRowAsync(IdentityDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = await db.TenantLimits.SingleOrDefaultAsync(l => l.TenantId == tenantId, cancellationToken);
        if (row is null)
        {
            row = new TenantLimitsRow { TenantId = tenantId, AllowedAliases = ["*"], UpdatedAt = now };
            db.TenantLimits.Add(row);
        }

        return row;
    }

    private static async Task<bool> TrySaveAsync(IdentityDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_tenant_name" })
        {
            return false;
        }
    }
}

internal sealed class EfApiKeyAdminStore(IDbContextFactory<IdentityDbContext> contexts, IAuditWriter audit) : IApiKeyAdminStore
{
    public async Task<IReadOnlyList<ApiKey>> ListAsync(Guid tenantId, int limit, (DateTimeOffset CreatedAt, Guid Id)? after, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var keys = db.ApiKeys.AsNoTracking().Where(k => k.TenantId == tenantId);
        if (after is { } cursor)
        {
            keys = keys.Where(k => k.CreatedAt < cursor.CreatedAt || (k.CreatedAt == cursor.CreatedAt && k.Id.CompareTo(cursor.Id) < 0));
        }

        return await keys.OrderByDescending(k => k.CreatedAt).ThenByDescending(k => k.Id).Take(limit + 1).ToListAsync(cancellationToken);
    }

    public async Task<ApiKey?> FindAsync(Guid tenantId, Guid keyId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.ApiKeys.AsNoTracking().SingleOrDefaultAsync(k => k.TenantId == tenantId && k.Id == keyId, cancellationToken);
    }

    public async Task<bool> AddAsync(ApiKey key, AuditEntry entry, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.ApiKeys.Add(key);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_api_key_key_prefix" })
        {
            return false;
        }

        await AdminWrite.CommitAsync(db, transaction, audit, entry, cancellationToken);
        return true;
    }

    public async Task<bool> RevokeAsync(Guid tenantId, Guid keyId, DateTimeOffset at, AuditEntry entry, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var changed = await db.ApiKeys
            .Where(k => k.TenantId == tenantId && k.Id == keyId && k.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(k => k.RevokedAt, at), cancellationToken);
        if (changed == 0)
        {
            return false;
        }

        await AdminWrite.CommitAsync(db, transaction, audit, entry, cancellationToken);
        return true;
    }
}
