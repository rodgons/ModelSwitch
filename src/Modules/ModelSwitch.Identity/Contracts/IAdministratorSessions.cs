using ModelSwitch.SharedKernel;

namespace ModelSwitch.Identity.Contracts;

/// <summary>
/// Server-side Administrator sessions (ADR-0011): sliding 8 h, absolute 24 h. The Api host's cookie ticket store is a thin
/// adapter over this. The principal is rebuilt from the database on every <see cref="ResolveAsync"/>, so a Role change or a
/// deactivation applies at once.
/// </summary>
public interface IAdministratorSessions
{
    /// <summary>Starts a session. <see cref="SessionInfo.SessionKey"/> is the cookie's secret; only its hash is stored.</summary>
    Task<SessionInfo> CreateAsync(Guid administratorId, CancellationToken cancellationToken);

    /// <summary>Null when the session is unknown, expired, revoked, or its Administrator is deactivated.</summary>
    Task<SessionInfo?> ResolveAsync(string sessionKey, CancellationToken cancellationToken);

    /// <summary>Slides the expiry forward, never beyond the absolute limit. Null when the session is no longer valid.</summary>
    Task<SessionInfo?> RenewAsync(string sessionKey, CancellationToken cancellationToken);

    Task RevokeAsync(string sessionKey, CancellationToken cancellationToken);
}

public sealed record SessionInfo(string SessionKey, AdministratorPrincipal Administrator, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);

public interface IAdministratorSignIn
{
    /// <summary>
    /// Verifies the credentials. A missing field is <c>validation_failed</c>; a wrong password, unknown account, deactivated
    /// account or lockout is the same <c>unauthenticated</c> answer, so the response reveals nothing. Records an Audit Record
    /// either way.
    /// </summary>
    Task<Result<AdministratorPrincipal>> SignInAsync(string? email, string? password, CancellationToken cancellationToken);
}

public enum BootstrapOutcome
{
    /// <summary>Neither variable was set.</summary>
    NotConfigured,

    /// <summary>An Administrator already existed, so nothing was changed.</summary>
    AlreadyBootstrapped,

    Created,
}

/// <summary>Creates the first Owner (security.md section 3). Used by the Migrator only.</summary>
public interface IOwnerBootstrapper
{
    /// <summary>
    /// Creates an Owner from <paramref name="email"/> and <paramref name="password"/> only when no Administrator exists.
    /// Idempotent and safe under concurrent migrators. An invalid value fails with <c>validation_failed</c>; the password
    /// is never part of a message.
    /// </summary>
    Task<Result<BootstrapOutcome>> BootstrapAsync(string? email, string? password, CancellationToken cancellationToken);
}
