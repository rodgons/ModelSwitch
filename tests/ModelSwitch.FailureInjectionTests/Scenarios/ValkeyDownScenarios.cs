using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.FailureInjectionTests.Support;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.SharedKernel;
using ModelSwitch.Usage.Contracts;
using Shouldly;
using Testcontainers.Redis;

namespace ModelSwitch.FailureInjectionTests.Scenarios;

/// <summary>
/// Base of the "Valkey stopped" scenarios (testing.md section 3). Each gets its own Valkey container, because the scenario stops it,
/// and a Tenant with limits, so that every request really needs Valkey. The Tenant has a Budget too large to refuse anything, so the
/// accounting assertions have a <c>budget_period</c> row to check.
/// </summary>
public abstract class ValkeyDownScenario(PostgresFixture postgres, ValkeyFixture valkey, string failureMode) : ScenarioTests, IAsyncLifetime
{
    private RedisContainer? _valkey;

    protected Guid LimitedTenantId { get; private set; }

    protected HttpClient Client { get; private set; } = null!;

    protected HttpClient LimitedClient { get; private set; } = null!;

    protected Task StopValkeyAsync() => _valkey!.StopAsync(TestContext.Current.CancellationToken);

    public async ValueTask InitializeAsync()
    {
        _valkey = await ValkeyFixture.StartDedicatedAsync();
        Gateway = new GatewayFixture(postgres, valkey) { ValkeyConnectionString = _valkey.GetConnectionString() };
        Gateway.Settings["Limits:FailureMode"] = failureMode;
        await Gateway.InitializeAsync();
        var (tenantId, key) = await Gateway.SeedTenantAsync("limited", requestsPerMinute: 6000, maxConcurrent: 50);
        LimitedTenantId = tenantId;
        await Gateway.Services.GetRequiredService<IBudgets>().SetMonthlyLimitAsync(tenantId, Money.FromUsd(1_000_000m), CancellationToken.None);
        LimitedClient = Gateway.Authenticated(key);
        Client = Gateway.Anonymous;
    }

    public async ValueTask DisposeAsync()
    {
        LimitedClient?.Dispose();
        if (Gateway is not null)
        {
            await Gateway.DisposeAsync();
        }

        if (_valkey is not null)
        {
            await _valkey.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Requests the Mock Provider has received so far, over every model.</summary>
    protected async Task<int> TotalProviderCallsAsync() => (await ProviderCallsAsync()).Values.Sum();

    protected static Task<HttpResponseMessage> ChatAsync(HttpClient client)
        => client.PostAsync(
            "/v1/chat/completions",
            new StringContent(
                new JsonObject
                {
                    ["model"] = GatewayFixture.ChatAlias,
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }),
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json"),
            TestContext.Current.CancellationToken);
}

/// <summary>testing.md section 3: "Valkey stopped (fail-closed)": 503 <c>limits_unavailable</c> and <c>/ready</c> = 503.</summary>
[Slow]
public sealed class ValkeyStoppedFailClosedScenario(PostgresFixture postgres, ValkeyFixture valkey) : ValkeyDownScenario(postgres, valkey, "Closed")
{
    [Fact]
    public async Task Requests_are_refused_with_limits_unavailable_and_the_instance_reports_not_ready()
    {
        (await ChatAsync(LimitedClient)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.GetAsync("/ready", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await StopValkeyAsync();
        var callsBefore = await TotalProviderCallsAsync();

        var response = await ChatAsync(LimitedClient);
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        response.Headers.Contains("Retry-After").ShouldBeTrue();
        response.Headers.Contains("X-Request-Id").ShouldBeTrue();
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("error").GetProperty("code").GetString().ShouldBe("limits_unavailable");
        problem.GetProperty("error").GetProperty("type").GetString().ShouldBe("api_error");

        var ready = await Client.GetAsync("/ready", TestContext.Current.CancellationToken);
        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await ready.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("checks").GetProperty("valkey").GetString().ShouldBe("unavailable");
        AmplificationReport.Measure("valkey stopped (fail-closed)", 1, await TotalProviderCallsAsync() - callsBefore, 0, "refused before routing");

        // Refused at the limits step, before the request is opened (architecture.md section 5), so nothing is recorded or reserved.
        var requestId = response.Headers.GetValues("X-Request-Id").Single();
        (await ScalarAsync($"select count(*)::text from usage.inference_request where id = '{requestId}'")).ShouldBe("0");
        await AssertConsistentBudgetAsync(LimitedTenantId);
    }
}

/// <summary>testing.md section 3: "Valkey stopped (fail-open)": requests succeed and the degraded metric increments.</summary>
[Slow]
public sealed class ValkeyStoppedFailOpenScenario(PostgresFixture postgres, ValkeyFixture valkey) : ValkeyDownScenario(postgres, valkey, "Open")
{
    [Fact]
    public async Task Requests_succeed_the_degraded_counter_increments_and_the_instance_stays_ready()
    {
        long degraded = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "modelswitch.limits.degraded")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref degraded, value));
        listener.Start();

        (await ChatAsync(LimitedClient)).StatusCode.ShouldBe(HttpStatusCode.OK);
        Interlocked.Read(ref degraded).ShouldBe(0);

        await StopValkeyAsync();
        var callsBefore = await TotalProviderCallsAsync();

        var requestIds = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var response = await ChatAsync(LimitedClient);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            requestIds.Add(response.Headers.GetValues("X-Request-Id").Single());
        }

        Interlocked.Read(ref degraded).ShouldBe(3);
        var ready = await Client.GetAsync("/ready", TestContext.Current.CancellationToken);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
        AmplificationReport.Measure("valkey stopped (fail-open)", 3, await TotalProviderCallsAsync() - callsBefore, 1, "healthy Provider: no retry");

        foreach (var requestId in requestIds)
        {
            (await RequestAsync(requestId))[0].ShouldBe("succeeded");
            await AssertConsistentAccountingAsync(requestId);
        }
    }
}
