using Microsoft.AspNetCore.Identity;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Identity.Domain;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Identity.Application;

internal sealed record CreateAdministratorRequest(string? Email, string? Password, string? Role, bool? AllTenants, IReadOnlyList<Guid>? TenantIds);

/// <summary>A null field is left unchanged. A <c>tenantIds</c> list replaces the grants.</summary>
internal sealed record UpdateAdministratorRequest(string? Role, bool? AllTenants, IReadOnlyList<Guid>? TenantIds, bool? Active, string? Password);

internal sealed record AdministratorView(
    Guid Id,
    string Email,
    string Role,
    bool AllTenants,
    IReadOnlyList<Guid> TenantIds,
    bool Active,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSignedInAt)
{
    public static AdministratorView From(Administrator administrator) => new(
        administrator.Id,
        administrator.Email,
        AdministratorRoles.ToWire(administrator.Role),
        administrator.AllTenants,
        [.. administrator.Grants.Select(g => g.TenantId).Order()],
        administrator.Active,
        administrator.CreatedAt,
        administrator.LastSignedInAt);
}

/// <summary>Administrator management (Owner only; the endpoint policy enforces the Role). Every change writes an Audit Record.</summary>
internal sealed class AdministratorService(
    IAdministratorStore store,
    IPasswordHasher<Administrator> hasher,
    IGuidGenerator ids,
    TimeProvider time)
{
    private const int _maxEmailLength = 254;

    public async Task<IReadOnlyList<AdministratorView>> ListAsync(CancellationToken cancellationToken)
        => [.. (await store.ListAsync(cancellationToken)).Select(AdministratorView.From)];

    public async Task<Result<AdministratorView>> CreateAsync(CreateAdministratorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        var email = ValidateEmail(request.Email, errors);
        if (PasswordPolicy.Violation(request.Password) is { } passwordProblem)
        {
            errors["password"] = [passwordProblem];
        }

        AdministratorRole role = default;
        if (!AdministratorRoles.TryParse(request.Role, out role))
        {
            errors["role"] = ["The role must be one of: viewer, operator, owner."];
        }

        var grant = ResolveGrant(role, request.AllTenants, request.TenantIds, errors);
        if (errors.Count == 0)
        {
            await CheckTenantsExistAsync(grant.TenantIds, errors, cancellationToken);
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The administrator is invalid.", errors);
        }

        var now = time.GetUtcNow();
        var administrator = new Administrator
        {
            Id = ids.NewGuid(),
            Email = email!,
            PasswordHash = string.Empty,
            Role = role,
            AllTenants = grant.AllTenants,
            CreatedAt = now,
            UpdatedAt = now,
            Grants = [.. grant.TenantIds.Select(tenantId => new AdministratorTenantGrant { TenantId = tenantId })],
        };
        administrator.PasswordHash = hasher.HashPassword(administrator, request.Password!);

        var entry = new AuditEntry(AuditActions.AdministratorCreated, "administrator", administrator.Id.ToString())
        {
            After = Summarise(administrator),
        };
        return await store.AddAsync(administrator, entry, cancellationToken)
            ? AdministratorView.From(administrator)
            : new Error(ErrorCode.Conflict, "An administrator with this email already exists.");
    }

    public async Task<Result<AdministratorView>> UpdateAsync(Guid id, UpdateAdministratorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var administrator = await store.FindByIdAsync(id, cancellationToken);
        if (administrator is null)
        {
            return new Error(ErrorCode.NotFound, "The administrator was not found.");
        }

        var errors = new Dictionary<string, string[]>();
        var role = administrator.Role;
        if (request.Role is not null && !AdministratorRoles.TryParse(request.Role, out role))
        {
            errors["role"] = ["The role must be one of: viewer, operator, owner."];
        }

        if (request.Password is not null && PasswordPolicy.Violation(request.Password) is { } passwordProblem)
        {
            errors["password"] = [passwordProblem];
        }

        // A field the request leaves out keeps its current value, so changing only the Role keeps the grant (an Owner's covers all Tenants).
        var allTenantsIn = request.AllTenants ?? administrator.AllTenants;
        var tenantIdsIn = request.TenantIds ?? (allTenantsIn ? null : administrator.Grants.Select(g => g.TenantId).ToList());
        var grant = ResolveGrant(role, allTenantsIn, tenantIdsIn, errors);
        if (errors.Count == 0)
        {
            await CheckTenantsExistAsync(grant.TenantIds, errors, cancellationToken);
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The administrator is invalid.", errors);
        }

        var active = request.Active ?? administrator.Active;
        var before = Summarise(administrator);
        var losesOwnership = administrator is { Role: AdministratorRole.Owner, Active: true } && (role != AdministratorRole.Owner || !active);
        if (losesOwnership && !await store.AnyOtherActiveOwnerAsync(id, cancellationToken))
        {
            return new Error(ErrorCode.Conflict, "The last active Owner cannot be demoted or deactivated.");
        }

        administrator.Role = role;
        administrator.AllTenants = grant.AllTenants;
        administrator.Grants = [.. grant.TenantIds.Select(tenantId => new AdministratorTenantGrant { AdministratorId = id, TenantId = tenantId })];
        administrator.Active = active;
        var after = Summarise(administrator);

        var passwordChanged = request.Password is not null;
        if (passwordChanged)
        {
            administrator.PasswordHash = hasher.HashPassword(administrator, request.Password!);
            administrator.ClearLockout();
        }

        var (changedBefore, changedAfter) = Diff(before, after);
        if (changedAfter.Count == 0 && !passwordChanged)
        {
            return AdministratorView.From(administrator);
        }

        if (passwordChanged)
        {
            changedAfter["passwordChanged"] = true;
        }

        administrator.UpdatedAt = time.GetUtcNow();
        await store.SaveAsync(
            administrator,
            new AuditEntry(AuditActions.AdministratorModified, "administrator", id.ToString()) { Before = changedBefore, After = changedAfter },
            cancellationToken);
        return AdministratorView.From(administrator);
    }

    /// <summary>The grant an Administrator of <paramref name="role"/> ends up with. An Owner always covers all Tenants.</summary>
    internal static (bool AllTenants, IReadOnlyList<Guid> TenantIds) ResolveGrant(
        AdministratorRole role,
        bool? allTenants,
        IReadOnlyList<Guid>? tenantIds,
        Dictionary<string, string[]> errors)
    {
        if (role == AdministratorRole.Owner)
        {
            return (true, []);
        }

        var ids = tenantIds?.Distinct().Order().ToList() ?? [];
        if (allTenants == true)
        {
            if (ids.Count > 0)
            {
                errors["tenantIds"] = ["The tenant list must be empty when allTenants is true."];
            }

            return (true, []);
        }

        return (false, ids);
    }

    internal static Dictionary<string, object?> Summarise(Administrator administrator) => new()
    {
        ["email"] = administrator.Email,
        ["role"] = AdministratorRoles.ToWire(administrator.Role),
        ["allTenants"] = administrator.AllTenants,
        ["tenantIds"] = administrator.Grants.Select(g => g.TenantId).Order().ToList(),
        ["active"] = administrator.Active,
    };

    private static (Dictionary<string, object?> Before, Dictionary<string, object?> After) Diff(Dictionary<string, object?> before, Dictionary<string, object?> after)
    {
        Dictionary<string, object?> changedBefore = [];
        Dictionary<string, object?> changedAfter = [];
        foreach (var (key, value) in after)
        {
            var old = before[key];
            var same = value is List<Guid> now && old is List<Guid> was ? now.SequenceEqual(was) : Equals(value, old);
            if (!same)
            {
                changedBefore[key] = old;
                changedAfter[key] = value;
            }
        }

        return (changedBefore, changedAfter);
    }

    private static string? ValidateEmail(string? email, Dictionary<string, string[]> errors)
    {
        var trimmed = email?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            errors["email"] = ["The email is required."];
            return null;
        }

        var at = trimmed.IndexOf('@', StringComparison.Ordinal);
        var valid = trimmed.Length <= _maxEmailLength
            && at > 0
            && at == trimmed.LastIndexOf('@')
            && trimmed.IndexOf('.', at + 1) > at + 1
            && !trimmed.EndsWith('.')
            && !trimmed.Any(char.IsWhiteSpace);
        if (!valid)
        {
            errors["email"] = ["The email must be a valid address."];
            return null;
        }

        return Administrator.NormalizeEmail(trimmed);
    }

    private async Task CheckTenantsExistAsync(IReadOnlyList<Guid> tenantIds, Dictionary<string, string[]> errors, CancellationToken cancellationToken)
    {
        if (tenantIds.Count == 0)
        {
            return;
        }

        var missing = await store.FindMissingTenantsAsync(tenantIds, cancellationToken);
        if (missing.Count > 0)
        {
            errors["tenantIds"] = [$"Unknown tenants: {string.Join(", ", missing)}."];
        }
    }
}
