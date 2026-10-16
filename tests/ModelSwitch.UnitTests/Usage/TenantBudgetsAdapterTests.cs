using System.Data.Common;
using Microsoft.Extensions.Time.Testing;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.Usage.Contracts;
using ModelSwitch.Usage.Infrastructure;
using Shouldly;

namespace ModelSwitch.UnitTests.Usage;

/// <summary>usage-and-budgets.md section 3: the Tenant detail reads the current Budget period's limit, reserved and spent amounts.</summary>
public sealed class TenantBudgetsAdapterTests
{
    private static readonly Guid _tenant = Guid.CreateVersion7();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 23, 30, 0, TimeSpan.Zero));
    private readonly StubBudgets _budgets = new();

    private TenantBudgetsAdapter Adapter() => new(_budgets, _time);

    [Fact]
    public async Task A_touched_period_reports_its_own_limit_reserved_and_spent_amounts()
    {
        _budgets.Current = new BudgetStatus(new DateOnly(2026, 10, 1), Money.FromUsd(10m), Money.FromUsd(0.25m), Money.FromUsd(1.5m));

        (await Adapter().GetCurrentPeriodAsync(_tenant, CancellationToken.None))
            .ShouldBe(new TenantBudgetPeriod(new DateOnly(2026, 10, 1), 10m, 0.25m, 1.5m));
    }

    [Fact]
    public async Task A_period_no_request_has_touched_is_this_utc_month_at_zero_against_the_standing_limit()
    {
        _budgets.Limit = Money.FromUsd(20m);

        (await Adapter().GetCurrentPeriodAsync(_tenant, CancellationToken.None))
            .ShouldBe(new TenantBudgetPeriod(new DateOnly(2026, 10, 1), 20m, 0m, 0m));
    }

    [Fact]
    public async Task Without_a_budget_the_period_has_no_limit()
        => (await Adapter().GetCurrentPeriodAsync(_tenant, CancellationToken.None)).Limit.ShouldBeNull();

    private sealed class StubBudgets : IBudgets
    {
        public BudgetStatus? Current { get; set; }

        public Money? Limit { get; set; }

        public Task SetMonthlyLimitAsync(Guid tenantId, Money? limit, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetMonthlyLimitAsync(Guid tenantId, Money? limit, DbTransaction transaction, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<Money?> GetMonthlyLimitAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(Limit);

        public Task<BudgetStatus?> GetCurrentAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(Current);
    }
}
