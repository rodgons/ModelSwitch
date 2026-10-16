using Microsoft.AspNetCore.Identity;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Identity.Domain;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Identity.Application;

/// <summary>
/// Password sign-in with lockout (security.md section 3). Every outcome that is not a success looks the same to the caller, and
/// an unknown account still costs one password hash, so neither the answer nor its timing reveals which accounts exist.
/// </summary>
internal sealed class AdministratorSignInService(
    IAdministratorStore administrators,
    IPasswordHasher<Administrator> hasher,
    IAuditWriter audit,
    TimeProvider time) : IAdministratorSignIn
{
    private static readonly Error _rejected = new(ErrorCode.Unauthenticated, "Invalid email or password.");

    // Verified against when the account does not exist, to spend the same time as a real check.
    private readonly Lazy<string> _decoyHash = new(() => new PasswordHasher<Administrator>().HashPassword(Decoy, "decoy-password-never-valid"));

    private static Administrator Decoy { get; } = new() { Email = "decoy", PasswordHash = string.Empty };

    public async Task<Result<AdministratorPrincipal>> SignInAsync(string? email, string? password, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(email))
        {
            errors["email"] = ["The email is required."];
        }

        if (string.IsNullOrEmpty(password))
        {
            errors["password"] = ["The password is required."];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The sign-in request is invalid.", errors);
        }

        var now = time.GetUtcNow();
        var administrator = await administrators.FindByEmailAsync(Administrator.NormalizeEmail(email!), cancellationToken);
        if (administrator is null)
        {
            hasher.VerifyHashedPassword(Decoy, _decoyHash.Value, password!);
            await audit.WriteAsync(Failed(actor: null, targetId: null, "unknown_account"), cancellationToken);
            return _rejected;
        }

        if (administrator.IsLockedOut(now))
        {
            await audit.WriteAsync(Failed(administrator.Id, administrator.Id.ToString(), "locked_out"), cancellationToken);
            return _rejected;
        }

        var verification = hasher.VerifyHashedPassword(administrator, administrator.PasswordHash, password!);
        if (verification == PasswordVerificationResult.Failed || !administrator.Active)
        {
            if (!administrator.Active)
            {
                await audit.WriteAsync(Failed(administrator.Id, administrator.Id.ToString(), "inactive"), cancellationToken);
                return _rejected;
            }

            administrator.RegisterFailedSignIn(now);
            var failed = Failed(administrator.Id, administrator.Id.ToString(), administrator.IsLockedOut(now) ? "invalid_password_locked" : "invalid_password");
            await administrators.SaveAsync(administrator, failed, cancellationToken);
            return _rejected;
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            administrator.PasswordHash = hasher.HashPassword(administrator, password!);
        }

        administrator.RegisterSuccessfulSignIn(now);
        administrator.UpdatedAt = now;
        await administrators.SaveAsync(
            administrator,
            new AuditEntry(AuditActions.AdministratorSignedIn, "administrator", administrator.Id.ToString()) { ActorAdministratorId = administrator.Id },
            cancellationToken);
        return administrator.ToPrincipal();
    }

    private static AuditEntry Failed(Guid? actor, string? targetId, string reason) => new(AuditActions.AdministratorSignInFailed, "administrator", targetId)
    {
        ActorAdministratorId = actor,
        Details = new Dictionary<string, object?> { ["reason"] = reason },
    };
}
