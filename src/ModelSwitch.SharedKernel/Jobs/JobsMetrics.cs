using System.Data.Common;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelSwitch.SharedKernel.Telemetry;

namespace ModelSwitch.SharedKernel.Jobs;

/// <summary>
/// <c>modelswitch.outbox.lag</c> and <c>modelswitch.jobs.queue_depth</c> (observability.md section 3). Observable gauges are read on the
/// exporter's thread, so they never query the database: a background sampler refreshes the values every <see cref="Interval"/>.
/// </summary>
internal sealed partial class JobsMetricsSampler : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly DbDataSource _dataSource;
    private readonly TimeProvider _time;
    private readonly ILogger<JobsMetricsSampler> _logger;
    private volatile Sample _sample = new(0, new Dictionary<string, long>());

    public JobsMetricsSampler(DbDataSource dataSource, TimeProvider time, IMeterFactory meters, ILogger<JobsMetricsSampler> logger)
    {
        ArgumentNullException.ThrowIfNull(meters);
        _dataSource = dataSource;
        _time = time;
        _logger = logger;
        var meter = meters.Create(GatewayTelemetry.Name);
        meter.CreateObservableGauge("modelswitch.outbox.lag", () => _sample.OutboxLagSeconds, "s", "Age of the oldest unpublished outbox event.");
        meter.CreateObservableGauge(
            "modelswitch.jobs.queue_depth",
            () => _sample.PendingByKind.Select(p => new Measurement<long>(p.Value, new KeyValuePair<string, object?>(GatewayTelemetry.Labels.Kind, p.Key))),
            "{job}",
            "Pending jobs by kind.");
    }

    private sealed record Sample(double OutboxLagSeconds, IReadOnlyDictionary<string, long> PendingByKind);

    /// <summary>Reads both values once. Internal for tests.</summary>
    internal async Task SampleAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT min(occurred_at) FROM jobs.outbox_event WHERE published_at IS NULL";
        var oldest = await command.ExecuteScalarAsync(cancellationToken);
        var lag = oldest is DateTime occurred ? Math.Max(0, (_time.GetUtcNow() - new DateTimeOffset(DateTime.SpecifyKind(occurred, DateTimeKind.Utc))).TotalSeconds) : 0;

        command.CommandText = "SELECT kind, count(*) FROM jobs.job WHERE status = 'pending' GROUP BY kind";
        var pending = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                pending[reader.GetString(0)] = reader.GetInt64(1);
            }
        }

        _sample = new Sample(lag, pending);
    }

    internal double OutboxLagSeconds => _sample.OutboxLagSeconds;

    internal IReadOnlyDictionary<string, long> PendingByKind => _sample.PendingByKind;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SampleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failed sample keeps the previous values; metrics must never stop the worker.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogFailed(_logger, exception);
            }

            await JobWorkerService.DelayQuietlyAsync(Interval, _time, stoppingToken);
        }
    }

    [LoggerMessage(EventId = 7001, Level = LogLevel.Warning, Message = "Sampling the outbox and job queue metrics failed; the previous values are kept.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
