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

    /// <summary>
    /// The current calendar month (UTC) of the Tenant's Budget: its limit, the amount held by requests in flight, and the settled
    /// Estimated Cost. A month no request has touched yet reads as zero against the standing limit.
    /// </summary>
    Task<TenantBudgetPeriod> GetCurrentPeriodAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>One Budget period in USD (<c>usage.budget_period</c>). The Reservation refuses a request when spent + reserved would pass the limit.</summary>
/// <param name="PeriodStart">The first day of the month.</param>
/// <param name="Limit">Null: no Budget.</param>
/// <param name="Reserved">Held by Reservations of requests in flight.</param>
/// <param name="Spent">Settled Estimated Cost.</param>
public sealed record TenantBudgetPeriod(DateOnly PeriodStart, decimal? Limit, decimal Reserved, decimal Spent);
