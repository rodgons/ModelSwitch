using System.Data.Common;
using System.Globalization;

namespace ModelSwitch.SharedKernel.Configuration;

/// <summary>
/// The write side of runtime configuration propagation (architecture.md section 6). Every admin write to runtime
/// configuration calls <see cref="BumpAsync"/> inside the transaction that makes the change: the version increment and the
/// <c>NOTIFY config_changed</c> commit (or roll back) together with it, so an api instance never rebuilds its
/// <c>ConfigurationSnapshot</c> from a half-written change. PostgreSQL delivers the notification at commit.
/// </summary>
public static class ConfigVersion
{
    /// <summary>The PostgreSQL notification channel instances <c>LISTEN</c> on.</summary>
    public const string Channel = "config_changed";

    /// <summary>The single-row table that holds the version (the <c>config</c> schema).</summary>
    public const string Table = "config.config_version";

    /// <summary>
    /// Increments the version and issues <c>NOTIFY config_changed</c> on <paramref name="connection"/>, within
    /// <paramref name="transaction"/> when given. Returns the new version. Pass the writing module's own open connection and
    /// transaction (for EF Core: <c>Database.GetDbConnection()</c> and <c>CurrentTransaction.GetDbTransaction()</c>).
    /// </summary>
    public static async Task<long> BumpAsync(DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        long version;
        await using (var bump = connection.CreateCommand())
        {
            bump.Transaction = transaction;
            bump.CommandText = $"UPDATE {Table} SET version = version + 1 RETURNING version";
            version = ToVersion(await bump.ExecuteScalarAsync(cancellationToken));
        }

        await using (var notify = connection.CreateCommand())
        {
            notify.Transaction = transaction;
            notify.CommandText = $"SELECT pg_notify('{Channel}', '{version.ToString(CultureInfo.InvariantCulture)}')";
            await notify.ExecuteNonQueryAsync(cancellationToken);
        }

        return version;
    }

    /// <summary>Reads the current version.</summary>
    public static async Task<long> ReadAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT version FROM {Table}";
        return ToVersion(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static long ToVersion(object? scalar) =>
        scalar is null or DBNull
            ? throw new InvalidOperationException($"{Table} has no row; run the Migrator.")
            : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
}
