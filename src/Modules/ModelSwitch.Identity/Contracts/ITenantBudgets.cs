using System.Data.Common;

namespace ModelSwitch.Identity.Contracts;

/// <summary>
/// The Tenant's monthly Budget (USD) as the admin limits endpoint sees it. Identity owns the endpoint but not the money: the Usage
/// module implements this, so the dependency points from Usage to Identity.
/// </summary>
public interface ITenantBudgets
{
    /// <summary>The standing monthly limit, or null when the Tenant has no Budget.</summary>
    Task<decimal?> GetMonthlyLimitAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Sets the monthly limit (null removes it) on the caller's PostgreSQL transaction, so it commits with the admin write.</summary>
    Task SetMonthlyLimitAsync(Guid tenantId, decimal? amount, DbTransaction transaction, CancellationToken cancellationToken);
}
