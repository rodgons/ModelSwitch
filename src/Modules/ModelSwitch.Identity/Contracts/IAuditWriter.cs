using System.Data.Common;

namespace ModelSwitch.Identity.Contracts;

/// <summary>Names of the audited actions (security.md section 3). Other modules add theirs as <c>noun.verb_past</c>.</summary>
public static class AuditActions
{
    public const string AdministratorSignedIn = "administrator.signed_in";
    public const string AdministratorSignInFailed = "administrator.sign_in_failed";
    public const string AdministratorSignedOut = "administrator.signed_out";
    public const string AdministratorCreated = "administrator.created";
    public const string AdministratorModified = "administrator.modified";
    public const string AdministratorBootstrapped = "administrator.bootstrapped";
    public const string TenantCreated = "tenant.created";
    public const string TenantModified = "tenant.modified";
    public const string TenantSuspended = "tenant.suspended";
    public const string TenantReactivated = "tenant.reactivated";
    public const string TenantLimitsChanged = "tenant.limits_changed";
    public const string TenantModelAccessChanged = "tenant.model_access_changed";
    public const string ApiKeyCreated = "api_key.created";
    public const string ApiKeyRevoked = "api_key.revoked";
}

/// <summary>
/// One sensitive administrative action. Values in <see cref="Before"/>, <see cref="After"/> and <see cref="Details"/> are short
/// summaries: never secrets, hashes, credentials or prompt or completion content (ADR-0010). Keys that look sensitive are
/// redacted and long strings are truncated by the writer, as a backstop and not as permission.
/// </summary>
public sealed record AuditEntry(string Action, string TargetType, string? TargetId)
{
    public Guid? TenantId { get; init; }

    /// <summary>The actor. Defaults to the current Administrator; set it for actions that happen before a session exists.</summary>
    public Guid? ActorAdministratorId { get; init; }

    public IReadOnlyDictionary<string, object?>? Before { get; init; }

    public IReadOnlyDictionary<string, object?>? After { get; init; }

    public IReadOnlyDictionary<string, object?>? Details { get; init; }
}

public interface IAuditWriter
{
    /// <summary>Writes the Audit Record in its own transaction.</summary>
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the Audit Record on the caller's transaction, so it commits or rolls back with the change it describes.
    /// The transaction must be on a PostgreSQL connection that can reach the <c>identity</c> schema.
    /// </summary>
    Task WriteAsync(AuditEntry entry, DbTransaction transaction, CancellationToken cancellationToken);
}
