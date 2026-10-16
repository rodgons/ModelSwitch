using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.SharedKernel.Jobs;
using ModelSwitch.Usage;
using ModelSwitch.Usage.Contracts;
using Npgsql;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Persistence;

/// <summary>usage-and-budgets.md section 6: the worker folds usage.request_completed into usage_hourly, exactly once per event.</summary>
public sealed class UsageRollupTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Guid _tenant = Guid.CreateVersion7();
    private static readonly Guid _model = Guid.CreateVersion7();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 10, 0, TimeSpan.Zero));
    private ServiceProvider _services = null!;
    private string _connectionString = null!;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateMigratedDatabaseAsync();
        _services = ModuleServices.ForJobs(_connectionString, _time, services => services.AddUsageModule());
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();

    private IUsageLedger Ledger => _services.GetRequiredService<IUsageLedger>();

    private OutboxRelay Relay => _services.GetRequiredService<OutboxRelay>();

    [Fact]
    public async Task Completed_requests_are_summed_into_their_hour_and_a_redelivered_event_changes_nothing()
    {
        await CompleteAsync(RequestOutcome.Succeeded, durationMs: 300, fallback: false, input: 100, output: 20);
        await CompleteAsync(RequestOutcome.Failed, durationMs: 3_000, fallback: true, input: 50, output: 0);

        (await Relay.RelayAsync(10, CancellationToken.None)).ShouldBe(2);

        var row = (await Rows(
            "select hour, requests, failed_requests, fallback_requests, attempts, input_tokens, output_tokens, estimated_cost::text, duration_ms_sum, latency_histogram::text " +
            $"from usage.usage_hourly where tenant_id = '{_tenant}'")).Single();
        row.ShouldBe(["10/15/2026 12:00:00", "2", "1", "1", "4", "150", "20", "0.0001900000", "3300", "{0,0,0,1,0,0,1,0,0,0,0,0,0}"]);

        // Redelivery: the relay already recorded both events in processed_event, so marking them unpublished again changes nothing.
        await Execute("update jobs.outbox_event set published_at = null");
        await Relay.RelayAsync(10, CancellationToken.None);
        (await Rows($"select requests from usage.usage_hourly where tenant_id = '{_tenant}'")).Single().ShouldBe(["2"]);
    }

    [Fact]
    public async Task A_request_that_reached_no_provider_is_rolled_up_under_the_empty_provider_model()
    {
        var opened = (await Ledger.OpenAsync(Open(Guid.CreateVersion7()), CancellationToken.None)).Value;
        await Ledger.SettleAsync(new SettleInferenceRequest(opened.RequestId, opened.StartedAt, RequestOutcome.Failed, "no_eligible_model", null, null, null, null, []), CancellationToken.None);

        await Relay.RelayAsync(10, CancellationToken.None);

        (await Rows($"select provider_model_id, requests, failed_requests, attempts from usage.usage_hourly where tenant_id = '{_tenant}'")).Single()
            .ShouldBe([Guid.Empty.ToString(), "1", "1", "0"]);
    }

    [Fact]
    public async Task Api_and_evaluation_requests_of_the_same_hour_alias_and_model_are_rolled_up_in_separate_rows()
    {
        await CompleteAsync(RequestOutcome.Succeeded, durationMs: 300, fallback: false, input: 100, output: 20);
        await CompleteAsync(RequestOutcome.Succeeded, durationMs: 300, fallback: false, input: 100, output: 20, RequestOrigin.Evaluation);
        await CompleteAsync(RequestOutcome.Failed, durationMs: 300, fallback: false, input: 10, output: 0, RequestOrigin.Evaluation);

        await Relay.RelayAsync(10, CancellationToken.None);

        (await Rows($"select origin, requests, failed_requests from usage.usage_hourly where tenant_id = '{_tenant}' order by origin"))
            .ShouldBe([["api", "1", "0"], ["evaluation", "2", "1"]]);
    }

    [Fact]
    public async Task An_event_written_before_origin_was_recorded_is_rolled_up_as_api_traffic()
    {
        var legacy =
            $$"""{"requestId":"{{Guid.CreateVersion7()}}","tenantId":"{{_tenant}}","requestedAlias":"legacy","status":"succeeded","usageStatus":"reported","providerModelId":"{{_model}}","selectedProviderModelId":"{{_model}}","attemptCount":1,"hadFallback":false,"inputTokens":5,"cachedInputTokens":null,"outputTokens":1,"estimatedCost":0.001,"currency":"USD","startedAt":"2026-10-15T11:30:00+00:00","durationMs":100,"ttftMs":null}""";
        await Execute($"insert into jobs.outbox_event (id, type, payload, occurred_at, attempts) values ('{Guid.CreateVersion7()}', 'usage.request_completed', '{legacy}', now(), 0)");

        (await Relay.RelayAsync(10, CancellationToken.None)).ShouldBe(1);

        (await Rows($"select origin, requests from usage.usage_hourly where tenant_id = '{_tenant}' and requested_alias = 'legacy'")).ShouldBe([["api", "1"]]);
    }

    [Fact]
    public async Task Long_range_usage_and_overview_answer_an_origin_filter_from_the_rollup()
    {
        // Whole hours three days ago exist only in the rollup (no raw rows), so these figures can only come from usage_hourly.
        var hour = new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture);
        await Execute(
            "insert into usage.usage_hourly (tenant_id, provider_model_id, requested_alias, origin, hour, requests, failed_requests, fallback_requests, attempts, input_tokens, " +
            "cached_input_tokens, output_tokens, estimated_cost, duration_ms_sum, latency_histogram, ttft_histogram) values " +
            $"('{_tenant}', '{_model}', 'balanced', 'api', '{hour}', 7, 1, 0, 7, 700, 0, 70, 0.7, 700, '{{0,0,7,0,0,0,0,0,0,0,0,0,0}}', '{{0,0,0,0,0,0,0,0,0,0,0,0,0}}'), " +
            $"('{_tenant}', '{_model}', 'balanced', 'evaluation', '{hour}', 3, 0, 0, 3, 300, 0, 30, 0.3, 300, '{{0,0,3,0,0,0,0,0,0,0,0,0,0}}', '{{0,0,0,0,0,0,0,0,0,0,0,0,0}}')");
        var queries = _services.GetRequiredService<IUsageQueries>();
        var from = _time.GetUtcNow().AddDays(-7);
        var to = _time.GetUtcNow();

        var evaluation = await queries.UsageAsync(
            new UsageQuery(new RequestFilter(null, TenantId: _tenant, Origin: RequestOrigin.Evaluation), from, to, UsageGrouping.None, UsageInterval.Month),
            CancellationToken.None);
        var both = await queries.UsageAsync(
            new UsageQuery(new RequestFilter(null, TenantId: _tenant), from, to, UsageGrouping.None, UsageInterval.Month),
            CancellationToken.None);
        var overview = await queries.OverviewAsync(new OverviewQuery(null, from, to, _tenant, RequestOrigin.Api), CancellationToken.None);

        evaluation.Single().Requests.ShouldBe(3);
        evaluation.Single().EstimatedCost.ShouldBe(0.3m);
        both.Sum(r => r.Requests).ShouldBe(10);
        overview.Totals.Requests.ShouldBe(7);
        overview.Totals.FailedRequests.ShouldBe(1);
        overview.Totals.Exact.ShouldBeFalse();
    }

    private static OpenInferenceRequest Open(Guid id, RequestOrigin origin = RequestOrigin.Api)
        => origin == RequestOrigin.Api
            ? new(id, _tenant, RequestOrigin.Api, Guid.CreateVersion7(), null, null, "balanced", null, false, 10, string.Empty)
            : new(id, _tenant, RequestOrigin.Evaluation, null, Guid.CreateVersion7(), null, "balanced", null, false, 10, string.Empty);

    private async Task CompleteAsync(RequestOutcome outcome, int durationMs, bool fallback, int input, int output, RequestOrigin origin = RequestOrigin.Api)
    {
        var opened = (await Ledger.OpenAsync(Open(Guid.CreateVersion7(), origin), CancellationToken.None)).Value;
        var started = opened.StartedAt;
        var pricing = new AttemptPricing(Guid.CreateVersion7(), new PriceRates(1m, null, 2m));
        AttemptRecord[] attempts =
        [
            new(1, Guid.CreateVersion7(), Guid.CreateVersion7(), AttemptKind.Primary, AttemptStatus.Failed, AttemptErrorClass.Transient, "provider_unavailable", 503, null,
                started, started.AddMilliseconds(10), null, 0, 0, UsageSource.Reported, Pricing: pricing),
            new(2, Guid.CreateVersion7(), _model, fallback ? AttemptKind.Fallback : AttemptKind.Retry,
                outcome == RequestOutcome.Succeeded ? AttemptStatus.Succeeded : AttemptStatus.Failed, null, null, null, null,
                started, started.AddMilliseconds(20), null, input, output, UsageSource.Reported, Pricing: pricing),
        ];

        _time.Advance(TimeSpan.FromMilliseconds(durationMs));
        await Ledger.SettleAsync(
            new SettleInferenceRequest(opened.RequestId, started, outcome, outcome == RequestOutcome.Succeeded ? null : "provider_error", null, null, null, null, attempts),
            CancellationToken.None);
    }

    private async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

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
