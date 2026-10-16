using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ModelSwitch.SharedKernel;
using Npgsql;

namespace ModelSwitch.Usage.Infrastructure;

/// <summary>
/// The conditional statements of the Reservation model (usage-and-budgets.md section 3), run on the caller's own connection and
/// transaction. They are plain SQL because correctness under concurrency rests on a single atomic <c>UPDATE ... WHERE</c>.
/// </summary>
internal static class BudgetSql
{
    public const string UsdCurrency = Money.Usd;

    /// <summary>The first day of the UTC calendar month containing <paramref name="instant"/>.</summary>
    public static DateOnly PeriodOf(DateTimeOffset instant) => new(instant.UtcDateTime.Year, instant.UtcDateTime.Month, 1);

    /// <summary>Lazily creates the Tenant's period row, taking the limit from the standing Budget, if any.</summary>
    public static Task EnsurePeriodAsync(UsageDbContext db, Guid tenantId, DateOnly periodStart, CancellationToken cancellationToken)
        => ExecuteAsync(
            db,
            """
            INSERT INTO usage.budget_period (tenant_id, period_start, limit_amount, reserved_amount, spent_amount, currency)
            SELECT @tenant, @period, (SELECT limit_amount FROM usage.budget WHERE tenant_id = @tenant), 0, 0, @currency
            ON CONFLICT (tenant_id, period_start) DO NOTHING
            """,
            cancellationToken,
            ("tenant", tenantId),
            ("period", periodStart),
            ("currency", UsdCurrency));

    /// <summary>
    /// Adds <paramref name="amount"/> to <c>reserved_amount</c> only if reserved + settled + amount stays within the limit (or there is
    /// none). False when the Budget cannot take it. Concurrent callers serialize on the row, so the check is never stale.
    /// </summary>
    public static async Task<bool> TryHoldAsync(UsageDbContext db, Guid tenantId, DateOnly periodStart, decimal amount, CancellationToken cancellationToken)
        => await ExecuteAsync(
            db,
            """
            UPDATE usage.budget_period
               SET reserved_amount = reserved_amount + @amount
             WHERE tenant_id = @tenant AND period_start = @period
               AND (limit_amount IS NULL OR spent_amount + reserved_amount + @amount <= limit_amount)
            """,
            cancellationToken,
            ("tenant", tenantId),
            ("period", periodStart),
            ("amount", amount)) == 1;

    /// <summary>Releases a hold and books the Estimated Cost actually settled.</summary>
    public static Task SettleAsync(UsageDbContext db, Guid tenantId, DateOnly periodStart, decimal held, decimal settled, CancellationToken cancellationToken)
        => ExecuteAsync(
            db,
            """
            UPDATE usage.budget_period
               SET reserved_amount = reserved_amount - @held, spent_amount = spent_amount + @settled
             WHERE tenant_id = @tenant AND period_start = @period
            """,
            cancellationToken,
            ("tenant", tenantId),
            ("period", periodStart),
            ("held", held),
            ("settled", settled));

    public static async Task<int> ExecuteAsync(UsageDbContext db, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(db, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static NpgsqlCommand Command(UsageDbContext db, string sql, params (string Name, object Value)[] parameters)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var command = new NpgsqlCommand(sql, connection, (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}
