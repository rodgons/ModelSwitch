using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.SharedKernel;
using ModelSwitch.Usage.Contracts;
using Npgsql;
using Shouldly;

[assembly: AssemblyFixture(typeof(PostgresFixture))]
[assembly: AssemblyFixture(typeof(ValkeyFixture))]

namespace ModelSwitch.FailureInjectionTests.Support;

/// <summary>What the gateway returned for one client request, with the wall-clock time until the response headers arrived.</summary>
public sealed record Outcome(string RequestId, int StatusCode, TimeSpan Elapsed, HttpResponseMessage Response);

/// <summary>
/// Base of the failure-injection scenarios. Each derived class is one scenario family and gets its own <see cref="GatewayFixture"/>
/// (its own database, Mock Provider and api host), so circuits and fault scripts never leak between scenarios. A scenario installs
/// a Routing Policy with small timeouts (<see cref="FastPolicy"/>) instead of waiting on the production defaults.
/// </summary>
public abstract class ScenarioTests
{
    /// <summary>
    /// Execution settings tuned for tests: a 5 s Deadline, 1 s Attempt timeout and a few milliseconds of backoff, so only a
    /// scenario that must wait (Retry-After) spends real time.
    /// </summary>
    /// <summary><see cref="FastPolicy"/>'s <c>maxAttempts</c>: no request may cost more Provider calls than this.</summary>
    protected const int FastPolicyMaxAttempts = 4;

    protected const string MaxAttemptsBound = "maxAttempts = 4";

    protected const string FastPolicy = """{"deadlineMs":5000,"attemptTimeoutMs":1000,"maxAttempts":4,"maxRetriesPerCandidate":1,"backoff":{"baseMs":5,"maxMs":10,"jitter":"full"}}""";

    private int _clientRequests;

    protected ScenarioTests(GatewayFixture gateway) => Gateway = gateway;

    /// <summary>For a scenario that builds its own <see cref="GatewayFixture"/> (for example on a dedicated database) and sets <see cref="Gateway"/>.</summary>
    protected ScenarioTests()
    {
    }

    protected GatewayFixture Gateway { get; set; } = null!;

    /// <summary>
    /// Resets the Mock Provider's faults and counters, and the client request count. Gives the Tenant a Budget too large to refuse
    /// anything: only a Tenant with a Budget has a <c>budget_period</c> row, and the accounting assertions check that row.
    /// </summary>
    protected async Task ResetAsync()
    {
        await Gateway.SetFaultAsync(null, null);
        await Gateway.Services.GetRequiredService<IBudgets>().SetMonthlyLimitAsync(Gateway.TenantId, Money.FromUsd(1_000_000m), CancellationToken.None);
        _clientRequests = 0;
    }

    /// <summary>Adds an alias over the given models of the OpenAI-protocol Provider, with an Execution policy.</summary>
    protected async Task<string> AddAliasAsync(string alias, string execution, params string[] models)
    {
        var candidates = new List<(string, string, Guid)>();
        foreach (var model in models)
        {
            candidates.Add(("mock-openai", model, await ProviderModelIdAsync(model)));
        }

        await Gateway.WriteConfigurationAsync(GatewayFixture.PolicyAndAliasSql(alias, [.. candidates], execution));
        return alias;
    }

    protected async Task<Guid> ProviderModelIdAsync(string model)
        => Guid.Parse((await ScalarAsync($"select m.id::text from registry.provider_model m join registry.provider p on p.id = m.provider_id where p.name = 'mock-openai' and m.provider_model_id = '{model}'"))!);

    protected async Task<Outcome> SendAsync(string alias, bool stream = false)
    {
        var body = new JsonObject
        {
            ["model"] = alias,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "one two three four five six seven eight" }),
        };
        if (stream)
        {
            body["stream"] = true;
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var clock = Stopwatch.StartNew();
        var response = await Gateway.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        clock.Stop();
        Interlocked.Increment(ref _clientRequests);
        return new Outcome(response.Headers.GetValues("X-Request-Id").Single(), (int)response.StatusCode, clock.Elapsed, response);
    }

    /// <summary>The Mock Provider's call count per model id (requests that reached it) since <see cref="ResetAsync"/>.</summary>
    protected async Task<IReadOnlyDictionary<string, int>> ProviderCallsAsync() => (await Gateway.MockStatsAsync()).Started;

    /// <summary>
    /// Computes this scenario's retry amplification from the Mock Provider's counters, publishes it to the test output and
    /// <see cref="AmplificationReport"/>, and asserts it stays within <paramref name="bound"/>.
    /// </summary>
    protected async Task<RetryAmplification> MeasureAmplificationAsync(string scenario, double bound, string boundReason)
    {
        var calls = (await ProviderCallsAsync()).Values.Sum();
        return AmplificationReport.Measure(scenario, _clientRequests, calls, bound, boundReason);
    }

    /// <summary>
    /// Accounting that must hold for any request (testing.md section 3): <c>inference_request.attempt_count</c> equals its Attempt rows,
    /// every Attempt that reached a Provider has exactly one <c>usage_record</c>, and the request's Tenant's <c>budget_period</c>
    /// rows hold the invariant of usage-and-budgets.md section 3 (see <see cref="AssertConsistentBudgetAsync(Guid)"/>).
    /// </summary>
    protected async Task AssertConsistentAccountingAsync(string requestId)
    {
        var attempts = await ScalarAsync($"select count(*)::text from usage.provider_attempt where inference_request_id = '{requestId}'");
        var counted = await ScalarAsync($"select attempt_count::text from usage.inference_request where id = '{requestId}'");
        attempts.ShouldBe(counted, "inference_request.attempt_count equals the Attempt rows");

        var called = await ScalarAsync($"select count(*)::text from usage.provider_attempt where inference_request_id = '{requestId}' and status <> 'skipped_circuit_open'");
        var records = await ScalarAsync($"select count(*)::text from usage.usage_record where inference_request_id = '{requestId}'");
        records.ShouldBe(called, "one usage_record per Attempt that reached a Provider");

        await AssertConsistentBudgetAsync(string.Format(CultureInfo.InvariantCulture, "(select tenant_id from usage.inference_request where id = '{0}')", requestId));
    }

    /// <summary>
    /// The Tenant-level half of <see cref="AssertConsistentAccountingAsync"/>, which also holds for a request the sweeper recovered (it
    /// has a Usage Record but no Attempt rows). Only a Tenant with a Budget has <c>budget_period</c> rows; it has one for the current
    /// month, and for every row <c>spent_amount</c> equals the Usage Records of the requests that started in that month while
    /// <c>reserved_amount</c> equals the Tenant's held Reservations of that month.
    /// </summary>
    protected Task AssertConsistentBudgetAsync(Guid tenantId) => AssertConsistentBudgetAsync($"'{tenantId}'");

    private async Task AssertConsistentBudgetAsync(string scope)
    {
        // One statement, so the figures come from one snapshot even while other requests are settling.
        var periods = await RowsAsync(
            "select p.period_start::text, p.spent_amount::text, " +
            "(select coalesce(sum(u.total_cost), 0) from usage.usage_record u join usage.inference_request q on q.id = u.inference_request_id " +
            " where u.tenant_id = p.tenant_id and q.started_at >= p.period_start::timestamp at time zone 'UTC' " +
            " and q.started_at < (p.period_start + interval '1 month') at time zone 'UTC')::text, " +
            "p.reserved_amount::text, " +
            "(select coalesce(sum(r.amount), 0) from usage.reservation r where r.tenant_id = p.tenant_id and r.period_start = p.period_start and r.status = 'held')::text, " +
            "date_trunc('month', now() at time zone 'UTC')::date = p.period_start " +
            $"from usage.budget_period p where p.tenant_id = {scope}");
        var hasBudget = await ScalarAsync($"select exists (select 1 from usage.budget where tenant_id = {scope})::text");
        if (hasBudget == "true")
        {
            periods.ShouldContain(p => p[5] == "True", "a Tenant with a Budget has a budget_period row for the current month");
        }

        foreach (var period in periods)
        {
            decimal.Parse(period[1], CultureInfo.InvariantCulture).ShouldBe(decimal.Parse(period[2], CultureInfo.InvariantCulture), $"budget_period.spent_amount of {period[0]} equals the Usage Records");
            decimal.Parse(period[3], CultureInfo.InvariantCulture).ShouldBe(decimal.Parse(period[4], CultureInfo.InvariantCulture), $"reserved_amount of {period[0]} equals the held Reservations");
        }
    }

    /// <summary>The Attempts of a request as <c>[kind, status, error_class, error_code, http_status, model]</c>, in order.</summary>
    protected Task<List<string[]>> AttemptsAsync(string requestId)
        => RowsAsync(
            "select kind, status, coalesce(error_class,''), coalesce(error_code,''), coalesce(http_status::text,''), " +
            "(select provider_model_id from registry.provider_model m where m.id = a.provider_model_id) " +
            $"from usage.provider_attempt a where inference_request_id = '{requestId}' order by attempt_number");

    /// <summary>The request row as <c>[status, error_code, attempt_count, had_fallback]</c>.</summary>
    protected async Task<string[]> RequestAsync(string requestId)
        => (await RowsAsync($"select status, coalesce(error_code,''), attempt_count::text, had_fallback::text from usage.inference_request where id = '{requestId}'")).Single();

    protected async Task<string?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Gateway.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    protected async Task<List<string[]>> RowsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Gateway.ConnectionString);
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

    /// <summary>Reads an SSE response to its end and returns the raw events.</summary>
    protected static async Task<List<string>> ReadEventsAsync(HttpResponseMessage response)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken), Encoding.UTF8);
        var events = new List<string>();
        var current = new StringBuilder();
        while (await reader.ReadLineAsync(TestContext.Current.CancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (current.Length > 0)
                {
                    events.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(current.Length == 0 ? string.Empty : "\n").Append(line);
        }

        if (current.Length > 0)
        {
            events.Add(current.ToString());
        }

        return events;
    }
}
