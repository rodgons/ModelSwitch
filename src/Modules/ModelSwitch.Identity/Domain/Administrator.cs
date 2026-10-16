using ModelSwitch.Identity.Contracts;

namespace ModelSwitch.Identity.Domain;

/// <summary>A human who signs into the dashboard (CONTEXT.md, Administrator). Local account; never an API Key.</summary>
internal sealed class Administrator
{
    /// <summary>Failures inside <see cref="LockoutWindow"/> that lock the account (security.md section 3).</summary>
    public const int MaxFailedSignIns = 5;

    public static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(15);

    public Guid Id { get; init; }

    /// <summary>Trimmed and lower-cased (<see cref="NormalizeEmail"/>); unique.</summary>
    public required string Email { get; set; }

    public required string PasswordHash { get; set; }

    public AdministratorRole Role { get; set; }

    public bool AllTenants { get; set; }

    /// <summary>A deactivated Administrator cannot sign in and every session of theirs stops working.</summary>
    public bool Active { get; set; } = true;

    public int FailedSignInCount { get; set; }

    public DateTimeOffset? FailedSignInWindowStartedAt { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public DateTimeOffset? LastSignedInAt { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<AdministratorTenantGrant> Grants { get; set; } = [];

    public TenantGrant Grant => AllTenants ? TenantGrant.AllTenants : TenantGrant.Only(Grants.Select(g => g.TenantId));

    public AdministratorPrincipal ToPrincipal() => new(Id, Email, Role, Grant);

    public static string NormalizeEmail(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return email.Trim().ToLowerInvariant();
    }

    public bool IsLockedOut(DateTimeOffset now) => LockedUntil is { } until && until > now;

    /// <summary>
    /// Counts a wrong password. The fifth failure inside <see cref="LockoutWindow"/> locks the account for the same period.
    /// A failure while locked changes nothing, so an attacker cannot keep an account locked by trying.
    /// </summary>
    public void RegisterFailedSignIn(DateTimeOffset now)
    {
        if (IsLockedOut(now))
        {
            return;
        }

        if (FailedSignInWindowStartedAt is not { } started || now - started >= LockoutWindow)
        {
            FailedSignInWindowStartedAt = now;
            FailedSignInCount = 0;
        }

        FailedSignInCount++;
        if (FailedSignInCount >= MaxFailedSignIns)
        {
            LockedUntil = now + LockoutWindow;
            FailedSignInCount = 0;
            FailedSignInWindowStartedAt = null;
        }
    }

    public void RegisterSuccessfulSignIn(DateTimeOffset now)
    {
        ClearLockout();
        LastSignedInAt = now;
    }

    /// <summary>Clears lockout state, e.g. after a successful sign-in or a password reset.</summary>
    public void ClearLockout()
    {
        FailedSignInCount = 0;
        FailedSignInWindowStartedAt = null;
        LockedUntil = null;
    }
}

internal sealed class AdministratorTenantGrant
{
    public Guid AdministratorId { get; init; }

    public Guid TenantId { get; init; }
}
