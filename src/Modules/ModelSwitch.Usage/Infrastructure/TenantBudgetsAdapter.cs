using System.Data.Common;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Usage.Infrastructure;

/// <summary>Exposes <see cref="IBudgets"/> to the Identity admin endpoints through Identity's own contract.</summary>
internal sealed class TenantBudgetsAdapter(IBudgets budgets, TimeProvider time) : ITenantBudgets
{
    public async Task<decimal?> GetMonthlyLimitAsync(Guid tenantId, CancellationToken cancellationToken)
        => (await budgets.GetMonthlyLimitAsync(tenantId, cancellationToken))?.Amount;

    public Task SetMonthlyLimitAsync(Guid tenantId, decimal? amount, DbTransaction transaction, CancellationToken cancellationToken)
        => budgets.SetMonthlyLimitAsync(tenantId, amount is { } value ? Money.FromUsd(value) : null, transaction, cancellationToken);

    public async Task<TenantBudgetPeriod> GetCurrentPeriodAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (await budgets.GetCurrentAsync(tenantId, cancellationToken) is { } current)
        {
            return new TenantBudgetPeriod(current.PeriodStart, current.Limit?.Amount, current.Reserved.Amount, current.Settled.Amount);
        }

        // EfBudgets always answers (it sums the ledger when there is no period row); any other IBudgets may not.
        var limit = await budgets.GetMonthlyLimitAsync(tenantId, cancellationToken);
        return new TenantBudgetPeriod(BudgetSql.PeriodOf(time.GetUtcNow()), limit?.Amount, 0m, 0m);
    }
}
