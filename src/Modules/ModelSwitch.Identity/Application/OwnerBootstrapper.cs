using Microsoft.AspNetCore.Identity;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Identity.Domain;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Identity.Application;

internal sealed class OwnerBootstrapper(
    IAdministratorStore store,
    IPasswordHasher<Administrator> hasher,
    IGuidGenerator ids,
    TimeProvider time) : IOwnerBootstrapper
{
    public async Task<Result<BootstrapOutcome>> BootstrapAsync(string? email, string? password, CancellationToken cancellationToken)
    {
        // Once any Administrator exists the variables are ignored, even invalid ones (deployment.md section 3).
        if (await store.AnyAsync(cancellationToken))
        {
            return BootstrapOutcome.AlreadyBootstrapped;
        }

        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrEmpty(password))
        {
            return BootstrapOutcome.NotConfigured;
        }

        var errors = new Dictionary<string, string[]>();
        var normalized = string.IsNullOrWhiteSpace(email) ? null : Administrator.NormalizeEmail(email);
        if (normalized is null || !normalized.Contains('@', StringComparison.Ordinal) || normalized.Contains(' ', StringComparison.Ordinal))
        {
            errors["MODELSWITCH__Bootstrap__OwnerEmail"] = ["It must be a valid email address."];
        }

        if (PasswordPolicy.Violation(password) is { } violation)
        {
            errors["MODELSWITCH__Bootstrap__OwnerPassword"] = [violation];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The bootstrap Owner settings are invalid.", errors);
        }

        var now = time.GetUtcNow();
        var owner = new Administrator
        {
            Id = ids.NewGuid(),
            Email = normalized!,
            PasswordHash = string.Empty,
            Role = AdministratorRole.Owner,
            AllTenants = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        owner.PasswordHash = hasher.HashPassword(owner, password!);

        var entry = new AuditEntry(AuditActions.AdministratorBootstrapped, "administrator", owner.Id.ToString())
        {
            After = AdministratorService.Summarise(owner),
        };
        return await store.AddFirstAsync(owner, entry, cancellationToken) ? BootstrapOutcome.Created : BootstrapOutcome.AlreadyBootstrapped;
    }
}
