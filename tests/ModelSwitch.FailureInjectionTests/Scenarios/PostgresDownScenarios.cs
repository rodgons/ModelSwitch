using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.FailureInjectionTests.Support;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.Usage.Contracts;
using Shouldly;

namespace ModelSwitch.FailureInjectionTests.Scenarios;

/// <summary>
/// testing.md section 3: "PostgreSQL stopped mid-load". Requests fail cleanly (Problem Details with a request id, in bounded time,
/// never a hang), requests already at the Provider still answer the client, and after the restart the sweeper reconciles what could
/// not be settled (usage-and-budgets.md section 3) so the accounting is consistent again. The scenario stops the database, so it runs
/// on a PostgreSQL container of its own; the assembly's shared one keeps serving every other class.
/// </summary>
[Slow]
public sealed class PostgresStoppedMidLoadScenario(ValkeyFixture valkey) : ScenarioTests, IAsyncLifetime
{
    private const int _inFlight = 6;
    private const int _whileDown = 5;
    private const int _providerLatencyMs = 1500;
    private const int _maxAttempts = 2;

    // Settlement retries for 10 s (InferenceService.SettleTimeout) before the request is left to the sweeper. A request is never
    // held longer than the Provider's answer plus that window; anything near the 20 s Deadline would mean something waited on the database.
    private static readonly TimeSpan _clean = TimeSpan.FromSeconds(15);

    private PostgresFixture? _postgres;

    public async ValueTask InitializeAsync()
    {
        _postgres = await PostgresFixture.StartDedicatedAsync();
        Gateway = new GatewayFixture(_postgres, valkey);
        await Gateway.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Gateway is not null)
        {
            await Gateway.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    [Fact]
    public async Task Requests_fail_cleanly_while_the_database_is_down_and_the_sweeper_reconciles_after_the_restart()
    {
        var token = TestContext.Current.CancellationToken;
        var alias = await AddAliasAsync(
            "spg",
            $$$"""{"deadlineMs":20000,"attemptTimeoutMs":5000,"maxAttempts":{{{_maxAttempts}}},"maxRetriesPerCandidate":1,"backoff":{"baseMs":5,"maxMs":10,"jitter":"full"}}""",
            GatewayFixture.PrimaryModel);
        (await SendAsync(alias)).StatusCode.ShouldBe(200, "the gateway works before the fault");
        await ResetAsync();
        await Gateway.SetFaultAsync(GatewayFixture.PrimaryModel, $"latency={_providerLatencyMs}");

        // Requests that have opened their Reservation and are waiting on the Provider when the database goes away.
        var inFlight = Enumerable.Range(0, _inFlight).Select(_ => SendAsync(alias)).ToArray();
        await WaitUntilAsync(async () => (await ProviderCallsAsync()).GetValueOrDefault(GatewayFixture.PrimaryModel) == _inFlight);
        await _postgres!.StopAsync(token);

        var refused = new List<Outcome>();
        for (var i = 0; i < _whileDown; i++)
        {
            refused.Add(await SendAsync(alias));
        }

        var answered = await Task.WhenAll(inFlight);

        // While the database is down: a clean error with a request id, quickly. Nothing reaches the Provider without a Reservation.
        foreach (var outcome in refused)
        {
            outcome.StatusCode.ShouldBeOneOf(500, 503);
            outcome.Response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
            outcome.RequestId.ShouldNotBeNullOrWhiteSpace();
            outcome.Elapsed.ShouldBeLessThan(_clean, "a request fails fast rather than waiting on the database");
        }

        // The requests already at the Provider still answer the client: settlement failing is never the client's problem.
        foreach (var outcome in answered)
        {
            outcome.StatusCode.ShouldBe(200);
            outcome.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(_providerLatencyMs) + _clean, "settlement gives up after its bounded window");
        }

        (await ProviderCallsAsync())[GatewayFixture.PrimaryModel].ShouldBe(_inFlight, "requests refused for the database never called the Provider");

        await _postgres.StartAsync(token);
        await WaitUntilAsync(async () => (await Gateway.Anonymous.GetAsync("/ready", token)).StatusCode == HttpStatusCode.OK, TimeSpan.FromSeconds(30));
        await Gateway.SetFaultAsync(null, null);
        var recovered = await SendAsync(alias);
        recovered.StatusCode.ShouldBe(200, "the gateway serves requests again once the database is back");
        await AssertConsistentAccountingAsync(recovered.RequestId);

        // The in-flight requests could not be settled: they are still running with their Reservations held. The sweeper takes over
        // once a Reservation expires (Deadline + 1 minute); the expiry is moved into the past instead of waiting for it.
        var ids = string.Join(',', answered.Select(o => $"'{o.RequestId}'"));
        (await ScalarAsync($"select count(*)::text from usage.inference_request where id in ({ids}) and status = 'running'")).ShouldBe(_inFlight.ToString(CultureInfo.InvariantCulture));
        await Gateway.ExecuteAsync($"update usage.reservation set expires_at = now() - interval '1 second' where status = 'held' and inference_request_id in ({ids})");
        var swept = await Gateway.Services.GetRequiredService<IReservationSweeper>().SweepAbandonedAsync(100, token);

        swept.ShouldBe(_inFlight);
        foreach (var outcome in answered)
        {
            (await RowsAsync(
                    "select q.status, q.usage_status, r.usage_source, (r.total_cost = v.amount)::text, v.status " +
                    "from usage.inference_request q join usage.usage_record r on r.inference_request_id = q.id join usage.reservation v on v.inference_request_id = q.id " +
                    $"where q.id = '{outcome.RequestId}'"))
                .ShouldBe([["abandoned", "unavailable", "unavailable", "true", "expired"]], "swept: possibly billed, so charged the held amount once");
        }

        (await ScalarAsync("select count(*)::text from usage.reservation where status = 'held'")).ShouldBe("0");
        await AssertConsistentBudgetAsync(Gateway.TenantId);

        // Client requests: the in-flight and refused ones. Each in-flight request made one call; a refused one made none.
        AmplificationReport.Measure("postgres stopped mid-load", _inFlight + _whileDown, _inFlight, _maxAttempts, $"maxAttempts = {_maxAttempts}");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = Environment.TickCount64 + (long)(timeout ?? TimeSpan.FromSeconds(10)).TotalMilliseconds;
        while (true)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not yet.
            }

            Environment.TickCount64.ShouldBeLessThan(deadline, "the condition was not met in time");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
