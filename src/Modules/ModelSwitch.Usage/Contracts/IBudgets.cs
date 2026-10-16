using System.Data.Common;
using ModelSwitch.SharedKernel;

namespace ModelSwitch.Usage.Contracts;

/// <summary>A Tenant's Budget for one calendar month (UTC), as the Reservation model sees it.</summary>
/// <param name="PeriodStart">The first day of the month.</param>
/// <param name="Limit">Null: no Budget, spending is unlimited.</param>
/// <param name="Reserved">Held by requests in flight.</param>
/// <param name="Spent">Settled Estimated Cost.</param>
public sealed record BudgetStatus(DateOnly PeriodStart, Money? Limit, Money Reserved, Money Spent);

/// <summary>Configures and reads Tenant Budgets (FR-07). The admin limits endpoint calls this.</summary>
public interface IBudgets
{
    /// <summary>
    /// Sets the Tenant's monthly limit, or removes the Budget when <paramref name="limit"/> is null. The current period's limit
    /// changes immediately; requests already holding a Reservation complete under it.
    /// </summary>
    Task SetMonthlyLimitAsync(Guid tenantId, Money? limit, CancellationToken cancellationToken);

    /// <summary>
    /// Like <see cref="SetMonthlyLimitAsync(Guid, Money?, CancellationToken)"/>, but on the caller's own PostgreSQL transaction, so the
    /// change commits or rolls back with the admin write and its Audit Record. The transaction's role must reach the <c>usage</c> schema.
    /// </summary>
    Task SetMonthlyLimitAsync(Guid tenantId, Money? limit, DbTransaction transaction, CancellationToken cancellationToken);

    /// <summary>The Tenant's standing monthly limit, or null when it has no Budget.</summary>
    Task<Money?> GetMonthlyLimitAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>The Tenant's current-month figures, or null when no request has touched the period.</summary>
    Task<BudgetStatus?> GetCurrentAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Recovers requests whose Reservation expired (the instance died or settlement failed for good).</summary>
public interface IReservationSweeper
{
    /// <summary>
    /// Settles up to <paramref name="batchSize"/> expired Reservations, each in its own transaction (usage-and-budgets.md section 3):
    /// the request becomes <c>abandoned</c>, one <c>unavailable</c> Usage Record charges the held amount, and the hold moves from
    /// reserved to spent. Returns how many were swept. The recurring job `usage.sweep_reservations` (every 30 s, in the worker) calls it.
    /// </summary>
    Task<int> SweepAbandonedAsync(int batchSize, CancellationToken cancellationToken);
}
