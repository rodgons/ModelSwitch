using Microsoft.EntityFrameworkCore;
using ModelSwitch.SharedKernel;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Usage.Infrastructure;

internal sealed class EfBudgets(IDbContextFactory<UsageDbContext> contexts, TimeProvider time) : IBudgets
{
    public async Task SetMonthlyLimitAsync(Guid tenantId, Money? limit, CancellationToken cancellationToken)
    {
        if (limit is { Amount: < 0 })
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "A Budget cannot be negative.");
        }

        var now = time.GetUtcNow();
        var period = BudgetSql.PeriodOf(now);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (limit is { } value)
        {
            await BudgetSql.ExecuteAsync(
                db,
                """
                INSERT INTO usage.budget (tenant_id, limit_amount, currency, updated_at) VALUES (@tenant, @limit, @currency, @now)
                ON CONFLICT (tenant_id) DO UPDATE SET limit_amount = EXCLUDED.limit_amount, updated_at = EXCLUDED.updated_at
                """,
                cancellationToken,
                ("tenant", tenantId),
                ("limit", value.Amount),
                ("currency", value.Currency),
                ("now", now));
        }
        else
        {
            await BudgetSql.ExecuteAsync(db, "DELETE FROM usage.budget WHERE tenant_id = @tenant", cancellationToken, ("tenant", tenantId));
        }

        // The current period follows at once; later periods pick the limit up when they are created.
        await BudgetSql.ExecuteAsync(
            db,
            "UPDATE usage.budget_period SET limit_amount = @limit WHERE tenant_id = @tenant AND period_start = @period",
            cancellationToken,
            ("tenant", tenantId),
            ("period", period),
            ("limit", (object?)limit?.Amount ?? DBNull.Value));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<BudgetStatus?> GetCurrentAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var period = BudgetSql.PeriodOf(time.GetUtcNow());
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.BudgetPeriods.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == tenantId && p.PeriodStart == period, cancellationToken);
        return row is null
            ? null
            : new BudgetStatus(
                row.PeriodStart,
                row.LimitAmount is { } limit ? Money.FromUsd(limit) : null,
                Money.FromUsd(row.ReservedAmount),
                Money.FromUsd(row.SpentAmount));
    }
}
