using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel.Configuration;
using Npgsql;

namespace ModelSwitch.Registry.Application;

/// <summary>
/// The tail every admin write shares (architecture.md section 6): the Audit Records and the config version bump with its
/// <c>NOTIFY</c> are written on the change's own transaction, so all of them commit or roll back together.
/// </summary>
internal static class ConfigWrite
{
    public static async Task CommitAsync(DbContext db, IDbContextTransaction transaction, IAuditWriter audit, IEnumerable<AuditEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(entries);
        var dbTransaction = transaction.GetDbTransaction();
        foreach (var entry in entries)
        {
            await audit.WriteAsync(entry, dbTransaction, cancellationToken);
        }

        await ConfigVersion.BumpAsync(db.Database.GetDbConnection(), dbTransaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>True for PostgreSQL's unique_violation (23505), which the services turn into <c>409 conflict</c>.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    }
}
