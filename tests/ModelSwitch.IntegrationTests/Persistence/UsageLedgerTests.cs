using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.Usage.Contracts;
using Npgsql;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Persistence;

/// <summary>Reserve, top-up, settle and sweep against a real database (usage-and-budgets.md section 3).</summary>
public sealed class UsageLedgerTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Guid _tenant = Guid.CreateVersion7();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private ServiceProvider _services = null!;
    private string _connectionString = null!;

    private IUsageLedger Ledger => _services.GetRequiredService<IUsageLedger>();

    private IBudgets Budgets => _services.GetRequiredService<IBudgets>();

    private IReservationSweeper Sweeper => _services.GetRequiredService<IReservationSweeper>();

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateMigratedDatabaseAsync();
        _services = ModuleServices.ForUsage(_connectionString, _time);
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();

    private static Money Usd(decimal amount) => Money.FromUsd(amount);

    private static OpenInferenceRequest Open(Guid id, decimal? hold, DateTimeOffset expiresAt)
        => new(id, _tenant, RequestOrigin.Api, Guid.CreateVersion7(), null, null, "balanced", null, false, 10, string.Empty, hold is { } h ? new ReservationRequest(Usd(h), expiresAt) : null);

    private static AttemptRecord Attempt(int number, int input, int output, UsageSource source = UsageSource.Reported, AttemptStatus status = AttemptStatus.Succeeded, Guid? model = null)
        => new(
            number,
            Guid.CreateVersion7(),
            model ?? Guid.CreateVersion7(),
            number == 1 ? AttemptKind.Primary : AttemptKind.Fallback,
            status,
            null,
            null,
            null,
            null,
            new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 15, 12, 0, 1, TimeSpan.Zero),
            null,
            input,
            output,
            source,
            Pricing: new AttemptPricing(Guid.CreateVersion7(), new PriceRates(1m, null, 2m)));

    // A Budget large enough never to refuse: only a Tenant with a Budget has a budget_period row (usage-and-budgets.md section 3).
    private Task WithBudgetAsync() => Budgets.SetMonthlyLimitAsync(_tenant, Usd(1_000m), CancellationToken.None);

    private static SettleInferenceRequest Settle(OpenedRequest opened, params AttemptRecord[] attempts)
        => new(opened.RequestId, opened.StartedAt, RequestOutcome.Succeeded, null, null, null, null, null, attempts, MaxOutputTokens: 1_000_000);

    [Fact]
    public async Task A_reservation_is_held_against_the_budget_and_settling_releases_it_and_books_the_spend()
    {
        await WithBudgetAsync();
        var id = Guid.CreateVersion7();
        var opened = (await Ledger.OpenAsync(Open(id, 0.5m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None)).Value;

        (await Scalar($"select reserved_amount from usage.budget_period where tenant_id = '{_tenant}'")).ShouldBe("0.5000000000");

        // 1,000,000 input at 1/M and 250,000 output at 2/M = 1.5
        await Ledger.SettleAsync(Settle(opened, Attempt(1, 1_000_000, 250_000)), CancellationToken.None);

        (await Rows($"select reserved_amount::text, spent_amount::text from usage.budget_period where tenant_id = '{_tenant}'")).Single()
            .ShouldBe(["0.0000000000", "1.5000000000"]);
        (await Scalar($"select status from usage.reservation where inference_request_id = '{id}'")).ShouldBe("settled");
        (await Rows($"select usage_source, input_cost::text, output_cost::text, total_cost::text, model_price_id is not null from usage.usage_record where inference_request_id = '{id}'")).Single()
            .ShouldBe(["reported", "1.0000000000", "0.5000000000", "1.5000000000", "True"]);
        (await Scalar($"select total_estimated_cost::text from usage.inference_request where id = '{id}'")).ShouldBe("1.5000000000");
        (await Scalar($"select estimated_cost::text from usage.provider_attempt where inference_request_id = '{id}'")).ShouldBe("1.5000000000");
    }

    [Fact]
    public async Task Settling_writes_the_completed_event_to_the_outbox_in_the_same_transaction()
    {
        var id = Guid.CreateVersion7();
        var opened = (await Ledger.OpenAsync(Open(id, 0.5m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None)).Value;
        await Ledger.SettleAsync(Settle(opened, Attempt(1, 100, 50)), CancellationToken.None);

        var events = await Rows($"select type, payload->>'requestId', payload->>'status', published_at is null from jobs.outbox_event where payload->>'requestId' = '{id}'");

        events.Single().ShouldBe(["usage.request_completed", id.ToString(), "succeeded", "True"]);
    }

    [Fact]
    public async Task Settling_twice_neither_duplicates_records_nor_moves_the_budget_again()
    {
        await WithBudgetAsync();
        var id = Guid.CreateVersion7();
        var opened = (await Ledger.OpenAsync(Open(id, 0.5m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None)).Value;
        var settlement = Settle(opened, Attempt(1, 1_000_000, 0));

        await Ledger.SettleAsync(settlement, CancellationToken.None);
        await Ledger.SettleAsync(settlement, CancellationToken.None);

        (await Scalar($"select count(*) from usage.usage_record where inference_request_id = '{id}'")).ShouldBe("1");
        (await Scalar($"select count(*) from jobs.outbox_event where payload->>'requestId' = '{id}'")).ShouldBe("1");
        (await Scalar($"select spent_amount::text from usage.budget_period where tenant_id = '{_tenant}'")).ShouldBe("1.0000000000");
    }

    [Fact]
    public async Task A_reservation_over_the_limit_is_refused_and_the_request_is_recorded_as_failed_without_a_hold()
    {
        await Budgets.SetMonthlyLimitAsync(_tenant, Usd(1m), CancellationToken.None);
        var id = Guid.CreateVersion7();

        var refused = await Ledger.OpenAsync(Open(id, 1.5m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None);

        refused.Error.Code.ShouldBe(ErrorCode.BudgetExceeded);
        (await Rows($"select status, error_code, usage_status from usage.inference_request where id = '{id}'")).Single().ShouldBe(["failed", "budget_exceeded", "reported"]);
        (await Scalar($"select count(*) from usage.reservation where inference_request_id = '{id}'")).ShouldBe("0");
        (await Scalar($"select count(*) from usage.budget_period where tenant_id = '{_tenant}' and reserved_amount <> 0")).ShouldBe("0");
    }

    [Fact]
    public async Task Reserved_plus_spent_is_what_the_limit_is_checked_against()
    {
        await Budgets.SetMonthlyLimitAsync(_tenant, Usd(1m), CancellationToken.None);
        var first = (await Ledger.OpenAsync(Open(Guid.CreateVersion7(), 0.6m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None)).Value;

        // 0.6 is reserved, so another 0.6 does not fit even though nothing is spent yet.
        (await Ledger.OpenAsync(Open(Guid.CreateVersion7(), 0.6m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None)).IsFailure.ShouldBeTrue();

        await Ledger.SettleAsync(Settle(first, Attempt(1, 400_000, 0)), CancellationToken.None);

        // 0.4 spent: 0.6 would reach 1.0 exactly, which is allowed.
        (await Ledger.OpenAsync(Open(Guid.CreateVersion7(), 0.6m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None)).IsSuccess.ShouldBeTrue();
        (await Ledger.OpenAsync(Open(Guid.CreateVersion7(), 0.01m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task A_top_up_raises_the_hold_when_the_budget_allows_and_is_refused_when_it_does_not()
    {
        await Budgets.SetMonthlyLimitAsync(_tenant, Usd(1m), CancellationToken.None);
        var id = Guid.CreateVersion7();
        await Ledger.OpenAsync(Open(id, 0.4m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None);

        (await Ledger.TopUpAsync(id, Usd(0.5m), CancellationToken.None)).IsSuccess.ShouldBeTrue();
        (await Scalar($"select amount::text from usage.reservation where inference_request_id = '{id}'")).ShouldBe("0.9000000000");

        var refused = await Ledger.TopUpAsync(id, Usd(0.2m), CancellationToken.None);

        refused.Error.Code.ShouldBe(ErrorCode.BudgetExceeded);
        (await Scalar($"select amount::text from usage.reservation where inference_request_id = '{id}'")).ShouldBe("0.9000000000");
        (await Scalar($"select reserved_amount::text from usage.budget_period where tenant_id = '{_tenant}'")).ShouldBe("0.9000000000");
    }

    [Fact]
    public async Task Settling_releases_the_topped_up_amount_in_full()
    {
        await WithBudgetAsync();
        var id = Guid.CreateVersion7();
        var opened = (await Ledger.OpenAsync(Open(id, 0.4m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None)).Value;
        await Ledger.TopUpAsync(id, Usd(0.5m), CancellationToken.None);

        await Ledger.SettleAsync(Settle(opened, Attempt(1, 100_000, 0)), CancellationToken.None);

        (await Rows($"select reserved_amount::text, spent_amount::text from usage.budget_period where tenant_id = '{_tenant}'")).Single().ShouldBe(["0.0000000000", "0.1000000000"]);
    }

    [Fact]
    public async Task Every_attempt_gets_a_record_and_an_ambiguous_one_is_charged_its_worst_case()
    {
        await WithBudgetAsync();
        var id = Guid.CreateVersion7();
        var opened = (await Ledger.OpenAsync(Open(id, 2m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None)).Value;
        var ambiguous = Attempt(1, 100_000, 0, UsageSource.Unavailable, AttemptStatus.Ambiguous) with { OutputTokens = null };
        var skipped = Attempt(2, 0, 0, status: AttemptStatus.SkippedCircuitOpen);

        await Ledger.SettleAsync(Settle(opened, ambiguous, skipped, Attempt(3, 1_000_000, 0)), CancellationToken.None);

        // Worst case of the ambiguous Attempt: 100,000 input at 1/M + 1,000,000 max output at 2/M = 2.1 ; then 1.0 reported.
        var records = await Rows($"select usage_source, estimate_method, total_cost::text from usage.usage_record where inference_request_id = '{id}' order by total_cost desc");
        records.ShouldBe([["unavailable", "reservation_upper_bound", "2.1000000000"], ["reported", string.Empty, "1.0000000000"]]);
        (await Scalar($"select usage_status from usage.inference_request where id = '{id}'")).ShouldBe("partial");
        (await Scalar($"select spent_amount::text from usage.budget_period where tenant_id = '{_tenant}'")).ShouldBe("3.1000000000");
    }

    [Fact]
    public async Task Changing_the_limit_applies_to_the_current_period_at_once_and_removing_it_lifts_the_cap()
    {
        await Ledger.OpenAsync(Open(Guid.CreateVersion7(), 0.5m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None);
        await Budgets.SetMonthlyLimitAsync(_tenant, Usd(0.6m), CancellationToken.None);

        (await Budgets.GetCurrentAsync(_tenant, CancellationToken.None))!.Limit!.Value.Amount.ShouldBe(0.6m);
        (await Ledger.OpenAsync(Open(Guid.CreateVersion7(), 0.2m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None)).IsFailure.ShouldBeTrue();

        await Budgets.SetMonthlyLimitAsync(_tenant, null, CancellationToken.None);

        (await Budgets.GetCurrentAsync(_tenant, CancellationToken.None))!.Limit.ShouldBeNull();
        (await Ledger.OpenAsync(Open(Guid.CreateVersion7(), 0.2m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_new_month_starts_a_new_period_with_the_standing_limit()
    {
        await Budgets.SetMonthlyLimitAsync(_tenant, Usd(1m), CancellationToken.None);
        var october = (await Ledger.OpenAsync(Open(Guid.CreateVersion7(), 0.9m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None)).Value;
        await Ledger.SettleAsync(Settle(october, Attempt(1, 900_000, 0)), CancellationToken.None);

        _time.SetUtcNow(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));

        (await Ledger.OpenAsync(Open(Guid.CreateVersion7(), 0.9m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None)).IsSuccess.ShouldBeTrue();
        (await Scalar($"select count(*) from usage.budget_period where tenant_id = '{_tenant}'")).ShouldBe("2");
    }

    [Fact]
    [TriggersError("internal_error")]
    public async Task The_sweeper_abandons_an_expired_request_and_charges_the_held_amount_as_unavailable_usage()
    {
        await WithBudgetAsync();
        var id = Guid.CreateVersion7();
        var opened = (await Ledger.OpenAsync(Open(id, 0.75m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None)).Value;

        (await Sweeper.SweepAbandonedAsync(10, CancellationToken.None)).ShouldBe(0, "the Reservation has not expired yet");

        _time.Advance(TimeSpan.FromMinutes(2));
        (await Sweeper.SweepAbandonedAsync(10, CancellationToken.None)).ShouldBe(1);

        (await Rows($"select status, error_code, usage_status, total_estimated_cost::text from usage.inference_request where id = '{id}'")).Single()
            .ShouldBe(["abandoned", "internal_error", "unavailable", "0.7500000000"]);
        (await Rows($"select usage_source, estimate_method, total_cost::text, input_tokens is null, provider_attempt_id is null from usage.usage_record where inference_request_id = '{id}'")).Single()
            .ShouldBe(["unavailable", "reservation_upper_bound", "0.7500000000", "True", "True"]);
        (await Rows($"select reserved_amount::text, spent_amount::text from usage.budget_period where tenant_id = '{_tenant}'")).Single().ShouldBe(["0.0000000000", "0.7500000000"]);
        (await Scalar($"select status from usage.reservation where inference_request_id = '{id}'")).ShouldBe("expired");
        (await Scalar($"select count(*) from jobs.outbox_event where payload->>'requestId' = '{id}' and payload->>'status' = 'abandoned'")).ShouldBe("1");

        // The late settlement of the original request is a no-op, and a second sweep finds nothing.
        await Ledger.SettleAsync(Settle(opened, Attempt(1, 1, 1)), CancellationToken.None);
        (await Sweeper.SweepAbandonedAsync(10, CancellationToken.None)).ShouldBe(0);
        (await Scalar($"select spent_amount::text from usage.budget_period where tenant_id = '{_tenant}'")).ShouldBe("0.7500000000");
    }

    [Fact]
    public async Task The_sweeper_leaves_a_settled_request_alone_and_honours_the_batch_size()
    {
        await WithBudgetAsync();
        var settled = (await Ledger.OpenAsync(Open(Guid.CreateVersion7(), 0.1m, _time.GetUtcNow().AddSeconds(5)), CancellationToken.None)).Value;
        await Ledger.SettleAsync(Settle(settled, Attempt(1, 10, 0)), CancellationToken.None);
        for (var i = 0; i < 3; i++)
        {
            await Ledger.OpenAsync(Open(Guid.CreateVersion7(), 0.1m, _time.GetUtcNow().AddSeconds(5)), CancellationToken.None);
        }

        _time.Advance(TimeSpan.FromMinutes(1));

        (await Sweeper.SweepAbandonedAsync(2, CancellationToken.None)).ShouldBe(2);
        (await Sweeper.SweepAbandonedAsync(2, CancellationToken.None)).ShouldBe(1);
        (await Scalar("select count(*) from usage.inference_request where status = 'abandoned'")).ShouldBe("3");
        (await Scalar($"select reserved_amount::text from usage.budget_period where tenant_id = '{_tenant}'")).ShouldBe("0.0000000000");
    }

    [Fact]
    public async Task Spent_always_equals_the_sum_of_the_usage_records_after_a_mix_of_settles_and_sweeps()
    {
        await WithBudgetAsync();
        var swept = Guid.CreateVersion7();
        await Ledger.OpenAsync(Open(swept, 0.3m, _time.GetUtcNow().AddSeconds(5)), CancellationToken.None);
        var opened = (await Ledger.OpenAsync(Open(Guid.CreateVersion7(), 0.3m, _time.GetUtcNow().AddMinutes(10)), CancellationToken.None)).Value;
        await Ledger.SettleAsync(Settle(opened, Attempt(1, 123_457, 7_891)), CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Sweeper.SweepAbandonedAsync(10, CancellationToken.None);

        var spent = decimal.Parse(await Scalar($"select spent_amount::text from usage.budget_period where tenant_id = '{_tenant}'") ?? "x", CultureInfo.InvariantCulture);
        var records = decimal.Parse(await Scalar($"select sum(total_cost)::text from usage.usage_record where tenant_id = '{_tenant}'") ?? "x", CultureInfo.InvariantCulture);

        spent.ShouldBe(records);
    }

    [Fact]
    public async Task A_tenant_without_a_budget_takes_no_budget_period_row_and_its_figures_come_from_the_ledger()
    {
        var id = Guid.CreateVersion7();
        var opened = (await Ledger.OpenAsync(Open(id, 0.5m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None)).Value;

        (await Scalar($"select count(*) from usage.budget_period where tenant_id = '{_tenant}'")).ShouldBe("0");
        (await Scalar($"select status from usage.reservation where inference_request_id = '{id}'")).ShouldBe("held", "the sweeper still needs the Reservation");
        var held = (await Budgets.GetCurrentAsync(_tenant, CancellationToken.None))!;
        (held.Limit, held.Reserved.Amount, held.Settled.Amount).ShouldBe((null, 0.5m, 0m));

        await Ledger.SettleAsync(Settle(opened, Attempt(1, 1_000_000, 250_000)), CancellationToken.None);

        (await Scalar($"select count(*) from usage.budget_period where tenant_id = '{_tenant}'")).ShouldBe("0");
        (await Scalar($"select status from usage.reservation where inference_request_id = '{id}'")).ShouldBe("settled");
        var settled = (await Budgets.GetCurrentAsync(_tenant, CancellationToken.None))!;
        (settled.Reserved.Amount, settled.Settled.Amount).ShouldBe((0m, 1.5m));
    }

    [Fact]
    public async Task A_top_up_without_a_budget_raises_the_hold_and_is_never_refused()
    {
        var id = Guid.CreateVersion7();
        await Ledger.OpenAsync(Open(id, 0.4m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None);

        (await Ledger.TopUpAsync(id, Usd(5m), CancellationToken.None)).IsSuccess.ShouldBeTrue();

        (await Scalar($"select amount::text from usage.reservation where inference_request_id = '{id}'")).ShouldBe("5.4000000000");
        (await Scalar($"select count(*) from usage.budget_period where tenant_id = '{_tenant}'")).ShouldBe("0");
    }

    [Fact]
    public async Task Setting_a_budget_mid_month_seeds_the_period_from_the_usage_records_and_the_held_reservations()
    {
        var first = (await Ledger.OpenAsync(Open(Guid.CreateVersion7(), 2m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None)).Value;
        await Ledger.SettleAsync(Settle(first, Attempt(1, 1_000_000, 250_000)), CancellationToken.None);
        var inFlight = (await Ledger.OpenAsync(Open(Guid.CreateVersion7(), 0.5m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None)).Value;

        await Budgets.SetMonthlyLimitAsync(_tenant, Usd(2.5m), CancellationToken.None);

        (await Rows($"select limit_amount::text, reserved_amount::text, spent_amount::text from usage.budget_period where tenant_id = '{_tenant}'")).Single()
            .ShouldBe(["2.5000000000", "0.5000000000", "1.5000000000"]);

        // 1.5 spent + 0.5 held: another 0.6 would pass 2.5.
        (await Ledger.OpenAsync(Open(Guid.CreateVersion7(), 0.6m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None)).IsFailure.ShouldBeTrue();

        // The Reservation opened before the Budget existed is released from the seeded period.
        await Ledger.SettleAsync(Settle(inFlight, Attempt(1, 100_000, 0)), CancellationToken.None);
        (await Rows($"select reserved_amount::text, spent_amount::text from usage.budget_period where tenant_id = '{_tenant}'")).Single()
            .ShouldBe(["0.0000000000", "1.6000000000"]);
    }

    [Fact]
    public async Task Removing_the_budget_drops_the_current_period_row_and_in_flight_requests_still_settle()
    {
        await Budgets.SetMonthlyLimitAsync(_tenant, Usd(10m), CancellationToken.None);
        var budgeted = (await Ledger.OpenAsync(Open(Guid.CreateVersion7(), 0.5m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None)).Value;

        await Budgets.SetMonthlyLimitAsync(_tenant, null, CancellationToken.None);

        (await Scalar($"select count(*) from usage.budget_period where tenant_id = '{_tenant}'")).ShouldBe("0");
        var unbudgeted = (await Ledger.OpenAsync(Open(Guid.CreateVersion7(), 50m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None)).Value;
        await Ledger.SettleAsync(Settle(budgeted, Attempt(1, 100_000, 0)), CancellationToken.None);

        // A Budget set again counts the Reservation still held.
        await Budgets.SetMonthlyLimitAsync(_tenant, Usd(100m), CancellationToken.None);
        (await Rows($"select reserved_amount::text, spent_amount::text from usage.budget_period where tenant_id = '{_tenant}'")).Single()
            .ShouldBe(["50.0000000000", "0.1000000000"]);
        await Ledger.SettleAsync(Settle(unbudgeted, Attempt(1, 200_000, 0)), CancellationToken.None);
        (await Rows($"select reserved_amount::text, spent_amount::text from usage.budget_period where tenant_id = '{_tenant}'")).Single()
            .ShouldBe(["0.0000000000", "0.3000000000"]);
    }

    [Fact]
    public async Task Concurrent_reservations_settles_and_budget_changes_keep_the_period_equal_to_the_ledger()
    {
        // Requests race with the Budget being set and removed; whatever interleaving happens, an existing period row must still
        // equal the Usage Records and the held Reservations of its month.
        var requests = Enumerable.Range(0, 40).Select(async i =>
        {
            var opened = await Ledger.OpenAsync(Open(Guid.CreateVersion7(), 0.01m, _time.GetUtcNow().AddMinutes(1)), CancellationToken.None);
            if (opened.IsSuccess && i % 4 != 0)
            {
                await Ledger.SettleAsync(Settle(opened.Value, Attempt(1, 1_000 * (i + 1), 0)), CancellationToken.None);
            }
        });
        var changes = Enumerable.Range(0, 10).Select(i => Budgets.SetMonthlyLimitAsync(_tenant, i % 3 == 2 ? null : Usd(1_000m), CancellationToken.None));
        await Task.WhenAll(requests.Concat(changes));
        await WithBudgetAsync();

        var period = (await Rows(
            $"select reserved_amount::text, spent_amount::text, " +
            $"(select coalesce(sum(amount), 0) from usage.reservation where tenant_id = '{_tenant}' and status = 'held')::text, " +
            $"(select coalesce(sum(total_cost), 0) from usage.usage_record where tenant_id = '{_tenant}')::text " +
            $"from usage.budget_period where tenant_id = '{_tenant}'")).Single();
        decimal.Parse(period[0], CultureInfo.InvariantCulture).ShouldBe(decimal.Parse(period[2], CultureInfo.InvariantCulture), "reserved equals the held Reservations");
        decimal.Parse(period[1], CultureInfo.InvariantCulture).ShouldBe(decimal.Parse(period[3], CultureInfo.InvariantCulture), "spent equals the Usage Records");
    }

    private async Task<string?> Scalar(string sql) => (await Rows(sql)).SingleOrDefault()?[0];

    private async Task<List<string[]>> Rows(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string[]>();
        while (await reader.ReadAsync())
        {
            rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!)]);
        }

        return rows;
    }
}
