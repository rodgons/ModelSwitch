using System.Diagnostics;
using System.Net;
using System.Text;
using ModelSwitch.FailureInjectionTests.Support;
using ModelSwitch.IntegrationTests.Infrastructure;
using Shouldly;

namespace ModelSwitch.FailureInjectionTests.Scenarios;

/// <summary>
/// observability.md section 5 (NFR-02, PRD Phase 4 "test telemetry degradation"): telemetry is best-effort. Whatever the OTLP backend
/// does (stops mid-load, hangs, or is unreachable at startup) the gateway starts, and inference succeeds at the same rate and without
/// waiting on the exporter.
/// </summary>
/// <summary>What the OTLP backend does when the api starts.</summary>
public enum BackendAtStartup
{
    Answering,
    Hanging,
    Down,
}

public abstract class TelemetryDegradationScenario(PostgresFixture postgres, ValkeyFixture valkey, BackendAtStartup backend) : IAsyncLifetime
{
    private const int _requestsPerPhase = 40;
    private const int _concurrency = 8;

    protected FakeOtlpBackend Backend { get; private set; } = null!;

    protected GatewayFixture Gateway { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Backend = await FakeOtlpBackend.StartAsync(hang: backend == BackendAtStartup.Hanging);
        if (backend == BackendAtStartup.Down)
        {
            // The endpoint is configured but nothing listens there: every export gets connection refused from the first one.
            await Backend.StopAsync();
        }

        Gateway = new GatewayFixture(postgres, valkey);
        Gateway.Settings["OTEL_EXPORTER_OTLP_ENDPOINT"] = Backend.Endpoint.ToString().TrimEnd('/');
        Gateway.Settings["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";

        // Export often, so the backend is really in use while the load runs; and time out quickly, as a production exporter would not.
        Gateway.Settings["OTEL_BSP_SCHEDULE_DELAY"] = "100";
        Gateway.Settings["OTEL_BLRP_SCHEDULE_DELAY"] = "100";
        Gateway.Settings["OTEL_METRIC_EXPORT_INTERVAL"] = "200";
        await Gateway.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Gateway is not null)
        {
            await Gateway.DisposeAsync();
        }

        if (Backend is not null)
        {
            await Backend.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Sends a phase of requests, <see cref="_concurrency"/> at a time, and returns each one's status and duration.</summary>
    protected async Task<List<(HttpStatusCode Status, TimeSpan Elapsed)>> LoadAsync()
    {
        var results = new List<(HttpStatusCode, TimeSpan)>();
        for (var sent = 0; sent < _requestsPerPhase; sent += _concurrency)
        {
            var wave = await Task.WhenAll(Enumerable.Range(0, _concurrency).Select(async _ =>
            {
                var clock = Stopwatch.StartNew();
                using var response = await Gateway.Client.PostAsync(
                    "/v1/chat/completions",
                    new StringContent("""{"model":"balanced","messages":[{"role":"user","content":"telemetry degradation"}]}""", Encoding.UTF8, "application/json"),
                    TestContext.Current.CancellationToken);
                return (response.StatusCode, clock.Elapsed);
            }));
            results.AddRange(wave);
        }

        return results;
    }

    /// <summary>Records the amplification of the whole class: the Provider is healthy, so telemetry trouble must not add calls.</summary>
    protected async Task MeasureAmplificationAsync(string scenario, int clientRequests)
        => AmplificationReport.Measure(scenario, clientRequests, (await Gateway.MockStatsAsync()).Started.Values.Sum(), 1, "healthy Provider: no retry");

    protected static double SuccessRate(List<(HttpStatusCode Status, TimeSpan Elapsed)> phase)
        => phase.Count(r => r.Status == HttpStatusCode.OK) / (double)phase.Count;

    protected static TimeSpan P95(List<(HttpStatusCode Status, TimeSpan Elapsed)> phase)
        => phase.Select(r => r.Elapsed).Order().ElementAt((int)Math.Ceiling(phase.Count * 0.95) - 1);

    protected async Task WaitForExportAsync(string signal)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (Backend.Exports(signal) == 0 && Environment.TickCount64 < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Backend.Exports(signal).ShouldBeGreaterThan(0, $"the gateway exported {signal} to the backend before it failed");
    }
}

[Slow]
public sealed class TelemetryBackendStoppedMidLoad(PostgresFixture postgres, ValkeyFixture valkey) : TelemetryDegradationScenario(postgres, valkey, BackendAtStartup.Answering)
{
    [Fact]
    public async Task Stopping_the_backend_during_load_does_not_change_the_inference_success_rate()
    {
        var before = await LoadAsync();
        await WaitForExportAsync("traces");

        // The backend goes away while a phase of requests is in flight, as when its container is killed under load.
        var during = LoadAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await Backend.StopAsync();
        var after = await during;

        SuccessRate(before).ShouldBe(1.0);
        SuccessRate(after).ShouldBe(SuccessRate(before));
        (await Gateway.Anonymous.GetAsync("/ready", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await MeasureAmplificationAsync("otlp backend stopped mid-load", before.Count + after.Count);
    }
}

[Slow]
public sealed class TelemetryBackendHangs(PostgresFixture postgres, ValkeyFixture valkey) : TelemetryDegradationScenario(postgres, valkey, BackendAtStartup.Hanging)
{
    [Fact]
    public async Task A_backend_that_never_answers_neither_blocks_startup_nor_slows_requests()
    {
        // InitializeAsync started the api against the hanging backend: startup succeeded.
        var phase = await LoadAsync();
        await WaitForExportAsync("traces");
        var second = await LoadAsync();

        SuccessRate(phase).ShouldBe(1.0);
        SuccessRate(second).ShouldBe(1.0);

        // The exporter's own timeout is 10 s: a request that waited on it would take at least that long.
        P95(second).ShouldBeLessThan(TimeSpan.FromSeconds(3));
        await MeasureAmplificationAsync("otlp backend hangs", phase.Count + second.Count);
    }
}

[Slow]
public sealed class TelemetryBackendDownAtStartup(PostgresFixture postgres, ValkeyFixture valkey) : TelemetryDegradationScenario(postgres, valkey, BackendAtStartup.Down)
{
    [Fact]
    public async Task The_gateway_starts_and_serves_requests_while_the_backend_is_unreachable()
    {
        // InitializeAsync started the api with an OTLP endpoint that refuses every connection.
        (await Gateway.Anonymous.GetAsync("/ready", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var phase = await LoadAsync();

        SuccessRate(phase).ShouldBe(1.0);
        P95(phase).ShouldBeLessThan(TimeSpan.FromSeconds(3));
        await MeasureAmplificationAsync("otlp backend down at startup", phase.Count);
    }
}
