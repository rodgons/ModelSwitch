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

    private static OpenInferenceRequest Open(Guid id)
        => new(id, _tenant, RequestOrigin.Api, Guid.CreateVersion7(), null, null, "balanced", null, false, 10, string.Empty);

    private async Task CompleteAsync(RequestOutcome outcome, int durationMs, bool fallback, int input, int output)
    {
        var opened = (await Ledger.OpenAsync(Open(Guid.CreateVersion7()), CancellationToken.None)).Value;
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
