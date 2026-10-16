using System.Data.Common;
using ModelSwitch.Identity.Application;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Identity.Domain;
using ModelSwitch.SharedKernel;

namespace ModelSwitch.UnitTests.Identity;

internal sealed class FakeAdministratorStore : IAdministratorStore
{
    public Dictionary<Guid, Administrator> Administrators { get; } = [];

    public HashSet<Guid> KnownTenants { get; } = [];

    public List<AuditEntry> Audits { get; } = [];

    public Administrator Add(string email, AdministratorRole role, bool active = true, string password = "correct horse battery", Guid[]? tenants = null)
    {
        var administrator = new Administrator
        {
            Id = Guid.CreateVersion7(),
            Email = email,
            PasswordHash = string.Empty,
            Role = role,
            AllTenants = role == AdministratorRole.Owner,
            Active = active,
            Grants = [.. (tenants ?? []).Select(t => new AdministratorTenantGrant { TenantId = t })],
        };
        administrator.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<Administrator>().HashPassword(administrator, password);
        Administrators[administrator.Id] = administrator;
        return administrator;
    }

    public Task<Administrator?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
        => Task.FromResult(Administrators.Values.FirstOrDefault(a => a.Email == normalizedEmail));

    public Task<Administrator?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(Administrators.GetValueOrDefault(id));

    public Task<IReadOnlyList<Administrator>> ListAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<Administrator>>([.. Administrators.Values]);

    public Task<bool> AnyAsync(CancellationToken cancellationToken) => Task.FromResult(Administrators.Count > 0);

    public Task<bool> AnyOtherActiveOwnerAsync(Guid exceptId, CancellationToken cancellationToken)
        => Task.FromResult(Administrators.Values.Any(a => a.Id != exceptId && a.Active && a.Role == AdministratorRole.Owner));

    public Task<IReadOnlyList<Guid>> FindMissingTenantsAsync(IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<Guid>>([.. tenantIds.Where(t => !KnownTenants.Contains(t))]);

    public Task<bool> AddAsync(Administrator administrator, AuditEntry audit, CancellationToken cancellationToken)
    {
        if (Administrators.Values.Any(a => a.Email == administrator.Email))
        {
            return Task.FromResult(false);
        }

        Administrators[administrator.Id] = administrator;
        Audits.Add(audit);
        return Task.FromResult(true);
    }

    public Task<bool> AddFirstAsync(Administrator administrator, AuditEntry audit, CancellationToken cancellationToken)
        => Administrators.Count > 0 ? Task.FromResult(false) : AddAsync(administrator, audit, cancellationToken);

    public Task SaveAsync(Administrator administrator, AuditEntry? audit, CancellationToken cancellationToken)
    {
        Administrators[administrator.Id] = administrator;
        if (audit is not null)
        {
            Audits.Add(audit);
        }

        return Task.CompletedTask;
    }
}

internal sealed class FakeSessionStore : ISessionStore
{
    public Dictionary<string, AdministratorSession> Sessions { get; } = [];

    public Task AddAsync(AdministratorSession session, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Sessions[session.Id] = session;
        return Task.CompletedTask;
    }

    public Task<AdministratorSession?> FindAsync(string id, CancellationToken cancellationToken)
        => Task.FromResult(Sessions.GetValueOrDefault(id));

    public Task UpdateExpiryAsync(string id, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        Sessions[id].ExpiresAt = expiresAt;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        Sessions.Remove(id);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingAuditWriter : IAuditWriter
{
    public List<AuditEntry> Entries { get; } = [];

    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task WriteAsync(AuditEntry entry, DbTransaction transaction, CancellationToken cancellationToken)
        => WriteAsync(entry, cancellationToken);
}

internal sealed class FixedCurrentAdministrator(AdministratorPrincipal? current) : ICurrentAdministrator
{
    public AdministratorPrincipal? Current { get; } = current;
}

internal sealed class VersionSevenGuids : IGuidGenerator
{
    public Guid NewGuid() => Guid.CreateVersion7();
}
