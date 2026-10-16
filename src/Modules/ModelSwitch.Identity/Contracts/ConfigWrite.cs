using System.Data.Common;
using ModelSwitch.SharedKernel.Configuration;
using Npgsql;

namespace ModelSwitch.Identity.Contracts;

/// <summary>
/// The tail every admin write shares (architecture.md section 6): the Audit Records and the config version bump with its
/// <c>NOTIFY</c> are written on the change's own transaction, so all of them commit or roll back together with it. The caller
/// commits afterwards. Modules use this instead of each keeping a copy.
/// </summary>
public static class ConfigWrite
{
    /// <summary>Writes <paramref name="entries"/> and bumps the config version on <paramref name="transaction"/>, without committing it.</summary>
    public static async Task StageAsync(DbTransaction transaction, IAuditWriter audit, IEnumerable<AuditEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
        {
            await audit.WriteAsync(entry, transaction, cancellationToken);
        }

        await ConfigVersion.BumpAsync(transaction.Connection ?? throw new ArgumentException("The transaction has no connection.", nameof(transaction)), transaction, cancellationToken);
    }

    /// <summary>True for PostgreSQL's unique_violation (23505) wrapped by EF Core, which the services turn into <c>409 conflict</c>.</summary>
    public static bool IsUniqueViolation(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    }
}
