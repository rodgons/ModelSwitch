using System.Data.Common;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Usage.Infrastructure;

/// <summary>Exposes <see cref="IBudgets"/> to the Identity admin endpoints through Identity's own contract.</summary>
internal sealed class TenantBudgetsAdapter(IBudgets budgets) : ITenantBudgets
{
    public async Task<decimal?> GetMonthlyLimitAsync(Guid tenantId, CancellationToken cancellationToken)
        => (await budgets.GetMonthlyLimitAsync(tenantId, cancellationToken))?.Amount;

    public Task SetMonthlyLimitAsync(Guid tenantId, decimal? amount, DbTransaction transaction, CancellationToken cancellationToken)
        => budgets.SetMonthlyLimitAsync(tenantId, amount is { } value ? Money.FromUsd(value) : null, transaction, cancellationToken);
}
