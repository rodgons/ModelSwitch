using ModelSwitch.Inference.Application;
using ModelSwitch.Usage.Contracts;
using Shouldly;

namespace ModelSwitch.UnitTests.Inference;

/// <summary>NFR-01: gateway overhead is the request's time outside Adapter calls and backoff waits (observability.md section 3).</summary>
public sealed class GatewayOverheadTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static AttemptRecord Attempt(int number, double startMs, double endMs, AttemptStatus status = AttemptStatus.Succeeded)
        => new(number, Guid.Empty, Guid.Empty, number == 1 ? AttemptKind.Primary : AttemptKind.Retry, status, null, null, null, null,
            _t0.AddMilliseconds(startMs), _t0.AddMilliseconds(endMs), null, null, null, UsageSource.Reported);

    private static RequestTelemetry Request(double durationMs, params AttemptRecord[] attempts)
        => new(Guid.Empty, "balanced", false, "succeeded", null, null, TimeSpan.FromMilliseconds(durationMs), null, attempts);

    [Fact]
    public void Overhead_excludes_the_window_from_the_first_attempt_start_to_the_last_attempt_end()
        => Request(500, Attempt(1, 10, 110, AttemptStatus.Failed), Attempt(2, 300, 480)).GatewayOverhead.ShouldBe(TimeSpan.FromMilliseconds(30));

    [Fact]
    public void A_request_without_attempts_is_all_overhead()
        => Request(12).GatewayOverhead.ShouldBe(TimeSpan.FromMilliseconds(12));

    [Fact]
    public void Attempts_skipped_for_an_open_circuit_do_not_count_as_provider_time()
        => Request(50, Attempt(1, 0, 0, AttemptStatus.SkippedCircuitOpen), Attempt(2, 20, 40)).GatewayOverhead.ShouldBe(TimeSpan.FromMilliseconds(30));

    [Fact]
    public void Overhead_is_never_negative()
        => Request(10, Attempt(1, 0, 20)).GatewayOverhead.ShouldBe(TimeSpan.Zero);
}

/// <summary>Regression: the second-valued histograms used the SDK's millisecond-sized default buckets, so p95 overhead read as ~5 s.</summary>
public sealed class InferenceMetricsBucketTests
{
    [Fact]
    public void Latency_histograms_advise_sub_second_bucket_bounds()
    {
        using var meters = new ModelSwitch.UnitTests.Support.TestMeterFactory();
        _ = new InferenceMetrics(meters);
        var advised = new Dictionary<string, IReadOnlyList<double>?>();
        using var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument is System.Diagnostics.Metrics.Histogram<double> histogram && instrument.Meter.Scope == meters)
                {
                    advised[instrument.Name] = histogram.Advice?.HistogramBucketBoundaries;
                }
            },
        };
        listener.Start();

        foreach (var name in new[] { "modelswitch.inference.duration", "modelswitch.inference.ttft", "modelswitch.inference.gateway_overhead" })
        {
            advised[name].ShouldNotBeNull().ShouldContain(0.05);
            advised[name]!.ShouldContain(0.1);
        }
    }
}
