using System.Diagnostics.Metrics;
using ModelSwitch.SharedKernel.Telemetry;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Inference.Application;

/// <summary>
/// The request-level instruments of observability.md section 3. Labels are bounded: alias, Provider and Provider Model ids (both
/// admin-configured), status, error code, Attempt kind and streaming. The Tenant is never a label.
/// </summary>
internal sealed class InferenceMetrics
{
    private readonly Counter<long> _requests;
    private readonly Histogram<double> _duration;
    private readonly Histogram<double> _ttft;
    private readonly Histogram<double> _overhead;
    private readonly Counter<long> _attempts;
    private readonly Counter<long> _fallbacks;
    private readonly Counter<long> _settlementFailures;

    public InferenceMetrics(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        var meter = meters.Create(GatewayTelemetry.Name);
        _requests = meter.CreateCounter<long>("modelswitch.inference.requests", "{request}", "Inference Requests that ended, by outcome.");
        _duration = meter.CreateHistogram<double>("modelswitch.inference.duration", "s", "Time from receipt to the end of the Inference Request.");
        _ttft = meter.CreateHistogram<double>("modelswitch.inference.ttft", "s", "Time to the First Token of a stream.");
        _overhead = meter.CreateHistogram<double>("modelswitch.inference.gateway_overhead", "s", "Request time outside Adapter calls and backoff waits (NFR-01).");
        _attempts = meter.CreateCounter<long>("modelswitch.attempts", "{attempt}", "Attempts, by Provider Model, kind and outcome.");
        _fallbacks = meter.CreateCounter<long>("modelswitch.fallbacks", "{fallback}", "Fallback Attempts.");
        _settlementFailures = meter.CreateCounter<long>("modelswitch.settlement.failures", "{request}", "Requests whose settlement gave up and was left to the sweeper.");
    }

    /// <summary>Records one finished Inference Request and its Attempts.</summary>
    public void Completed(RequestTelemetry request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var streaming = request.Streaming ? "true" : "false";
        var model = request.SelectedProviderModelId?.ToString() ?? string.Empty;
        _requests.Add(
            1,
            new(GatewayTelemetry.Labels.Alias, request.Alias),
            new(GatewayTelemetry.Labels.Model, model),
            new(GatewayTelemetry.Labels.Status, request.Status),
            new(GatewayTelemetry.Labels.ErrorCode, request.ErrorCode ?? string.Empty),
            new(GatewayTelemetry.Labels.Streaming, streaming));
        _duration.Record(
            request.Duration.TotalSeconds,
            new(GatewayTelemetry.Labels.Alias, request.Alias),
            new(GatewayTelemetry.Labels.Status, request.Status),
            new(GatewayTelemetry.Labels.Streaming, streaming));
        _overhead.Record(request.GatewayOverhead.TotalSeconds, new KeyValuePair<string, object?>(GatewayTelemetry.Labels.Streaming, streaming));
        if (request.Ttft is { } ttft)
        {
            _ttft.Record(ttft.TotalSeconds, new(GatewayTelemetry.Labels.Alias, request.Alias), new(GatewayTelemetry.Labels.Model, model));
        }

        foreach (var attempt in request.Attempts)
        {
            _attempts.Add(
                1,
                new(GatewayTelemetry.Labels.Provider, attempt.ProviderId.ToString()),
                new(GatewayTelemetry.Labels.Model, attempt.ProviderModelId.ToString()),
                new(GatewayTelemetry.Labels.AttemptKind, KindName(attempt.Kind)),
                new(GatewayTelemetry.Labels.Status, StatusName(attempt.Status)),
                new(GatewayTelemetry.Labels.ErrorCode, attempt.ErrorCode ?? string.Empty));
            if (attempt.Kind == AttemptKind.Fallback && attempt.Status != AttemptStatus.SkippedCircuitOpen)
            {
                _fallbacks.Add(1, new KeyValuePair<string, object?>(GatewayTelemetry.Labels.Alias, request.Alias));
            }
        }
    }

    public void SettlementFailed() => _settlementFailures.Add(1);

    internal static string KindName(AttemptKind kind) => kind switch
    {
        AttemptKind.Primary => "primary",
        AttemptKind.Retry => "retry",
        _ => "fallback",
    };

    internal static string StatusName(AttemptStatus status) => status switch
    {
        AttemptStatus.Succeeded => "succeeded",
        AttemptStatus.Failed => "failed",
        AttemptStatus.Cancelled => "cancelled",
        AttemptStatus.Ambiguous => "ambiguous",
        _ => "skipped_circuit_open",
    };
}

/// <summary>What the metrics and the completion log line need to know about a finished request. Metadata only.</summary>
internal sealed record RequestTelemetry(
    Guid RequestId,
    string Alias,
    bool Streaming,
    string Status,
    string? ErrorCode,
    Guid? SelectedProviderModelId,
    TimeSpan Duration,
    TimeSpan? Ttft,
    IReadOnlyList<AttemptRecord> Attempts)
{
    /// <summary>
    /// The request's time minus the window from the first Attempt's start to the last one's end, which holds every Adapter call and
    /// every backoff wait between them. A request with no Attempt is all overhead.
    /// </summary>
    public TimeSpan GatewayOverhead
    {
        get
        {
            DateTimeOffset? first = null;
            DateTimeOffset? last = null;
            foreach (var attempt in Attempts)
            {
                if (attempt.Status == AttemptStatus.SkippedCircuitOpen)
                {
                    continue;
                }

                first = first is null || attempt.StartedAt < first ? attempt.StartedAt : first;
                last = last is null || attempt.CompletedAt > last ? attempt.CompletedAt : last;
            }

            var overhead = first is { } start && last is { } end ? Duration - (end - start) : Duration;
            return overhead < TimeSpan.Zero ? TimeSpan.Zero : overhead;
        }
    }

    /// <summary>The number of Attempts that reached a Provider.</summary>
    public int CalledAttempts => Attempts.Count(a => a.Status != AttemptStatus.SkippedCircuitOpen);
}
