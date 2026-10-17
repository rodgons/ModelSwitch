using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ModelSwitch.SharedKernel;
using ModelSwitch.Usage.Contracts;
using Npgsql;

namespace ModelSwitch.Usage.Infrastructure;

internal sealed class EfBudgets(IDbContextFactory<UsageDbContext> contexts, TimeProvider time) : IBudgets
{
    public async Task SetMonthlyLimitAsync(Guid tenantId, Money? limit, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ApplyAsync(tenantId, limit, (NpgsqlTransaction)transaction.GetDbTransaction(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task SetMonthlyLimitAsync(Guid tenantId, Money? limit, DbTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return transaction is NpgsqlTransaction npgsqlTransaction
            ? ApplyAsync(tenantId, limit, npgsqlTransaction, cancellationToken)
            : throw new ArgumentException("The Budget transaction must be a PostgreSQL transaction.", nameof(transaction));
    }

    public async Task<Money?> GetMonthlyLimitAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.Budgets.AsNoTracking().FirstOrDefaultAsync(b => b.TenantId == tenantId, cancellationToken);
        return row is null ? null : new Money(row.LimitAmount, row.Currency);
    }

    public async Task<BudgetStatus?> GetCurrentAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var period = BudgetSql.PeriodOf(time.GetUtcNow());
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.BudgetPeriods.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == tenantId && p.PeriodStart == period, cancellationToken);
        if (row is not null)
        {
            return new BudgetStatus(
                row.PeriodStart,
                row.LimitAmount is { } limit ? Money.FromUsd(limit) : null,
                Money.FromUsd(row.ReservedAmount),
                Money.FromUsd(row.SettledAmount));
        }

        // No period row: the Tenant has had no Budget this month (or no request since it got one), so the figures come from the
        // Reservations and Usage Records themselves, computed exactly as a row would be seeded.
        var (from, to) = BudgetSql.Bounds(period);
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = BudgetSql.Command(
            db,
            """
            SELECT (SELECT limit_amount FROM usage.budget WHERE tenant_id = @tenant),
                   (SELECT coalesce(sum(r.amount), 0) FROM usage.reservation r
                     WHERE r.tenant_id = @tenant AND r.period_start = @period AND r.status = 'held'),
                   (SELECT coalesce(sum(u.total_cost), 0) FROM usage.usage_record u
                     WHERE u.tenant_id = @tenant AND u.recorded_at >= @period_from
                       AND EXISTS (SELECT 1 FROM usage.inference_request q
                                    WHERE q.id = u.inference_request_id AND q.started_at >= @period_from AND q.started_at < @period_to))
            """,
            ("tenant", tenantId),
            ("period", period),
            ("period_from", from),
            ("period_to", to));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new BudgetStatus(
            period,
            reader.IsDBNull(0) ? null : Money.FromUsd(reader.GetDecimal(0)),
            Money.FromUsd(reader.GetDecimal(1)),
            Money.FromUsd(reader.GetDecimal(2)));
    }

    /// <summary>
    /// Under the Tenant's exclusive Budget lock: a new or changed Budget creates the current period row from the month's Usage
    /// Records and held Reservations (or changes its limit), and removing the Budget deletes the row, so a Tenant without a Budget
    /// takes no row lock on the request path. Later periods are created by their first Reservation.
    /// </summary>
    private async Task ApplyAsync(Guid tenantId, Money? limit, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        if (limit is { Amount: < 0 })
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "A Budget cannot be negative.");
        }

        var connection = transaction.Connection ?? throw new ArgumentException("The Budget transaction has no connection.", nameof(transaction));
        var now = time.GetUtcNow();
        var period = BudgetSql.PeriodOf(now);
        (string, (string, object)[])[] statements = limit is { } value
            ?
            [
                (BudgetSql.ExclusiveLockSql, BudgetSql.LockParameters(tenantId)),
                (
                    """
                    INSERT INTO usage.budget (tenant_id, limit_amount, currency, updated_at) VALUES (@tenant, @limit, @currency, @now)
                    ON CONFLICT (tenant_id) DO UPDATE SET limit_amount = EXCLUDED.limit_amount, updated_at = EXCLUDED.updated_at
                    """,
                    [("tenant", tenantId), ("limit", value.Amount), ("currency", value.Currency), ("now", now)]),
                (
                    "UPDATE usage.budget_period SET limit_amount = @limit WHERE tenant_id = @tenant AND period_start = @period",
                    [("tenant", tenantId), ("period", period), ("limit", value.Amount)]),
                (BudgetSql.SeedPeriodSql, BudgetSql.SeedParameters(tenantId, period)),
            ]
            :
            [
                (BudgetSql.ExclusiveLockSql, BudgetSql.LockParameters(tenantId)),
                ("DELETE FROM usage.budget WHERE tenant_id = @tenant", [("tenant", tenantId)]),
                ("DELETE FROM usage.budget_period WHERE tenant_id = @tenant AND period_start = @period", [("tenant", tenantId), ("period", period)]),
            ];
        await using var batch = BudgetSql.Batch(connection, transaction, statements);
        await batch.ExecuteNonQueryAsync(cancellationToken);
    }
}
