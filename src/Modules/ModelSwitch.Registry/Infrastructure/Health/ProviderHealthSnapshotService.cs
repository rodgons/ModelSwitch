using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelSwitch.Registry.Contracts;

namespace ModelSwitch.Registry.Infrastructure.Health;

/// <summary>
/// Writes this instance's Provider Health to <c>registry.provider_health_snapshot</c> every <see cref="CircuitOptions.SnapshotInterval"/>
/// (ADR-0005), so the dashboard can show health per instance plus an aggregate. It runs in the process that owns the
/// breakers (the api host), because the circuits live in that process's memory. A failed write is logged and retried on
/// the next tick; it never affects inference.
/// </summary>
internal sealed partial class ProviderHealthSnapshotService(
    IProviderHealth health,
    IDbContextFactory<RegistryDbContext> contexts,
    CircuitOptions options,
    TimeProvider time,
    ILogger<ProviderHealthSnapshotService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.SnapshotInterval, time);
        try
        {
            do
            {
                await WriteOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Upserts one row per Provider Model this instance has called. Returns the number of rows written.</summary>
    internal async Task<int> WriteOnceAsync(CancellationToken cancellationToken)
    {
        var snapshot = health.Snapshot();
        if (snapshot.Count == 0)
        {
            return 0;
        }

        try
        {
            var capturedAt = time.GetUtcNow();
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            foreach (var entry in snapshot)
            {
                var state = StateName(entry.State);
                var rate = (float)entry.WindowFailureRate;
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO registry.provider_health_snapshot (instance_id, provider_model_id, circuit_state, window_failure_rate, window_calls, captured_at)
                    VALUES ({options.InstanceId}, {entry.ProviderModelId}, {state}, {rate}, {entry.WindowCalls}, {capturedAt})
                    ON CONFLICT (instance_id, provider_model_id) DO UPDATE
                    SET circuit_state = EXCLUDED.circuit_state,
                        window_failure_rate = EXCLUDED.window_failure_rate,
                        window_calls = EXCLUDED.window_calls,
                        captured_at = EXCLUDED.captured_at
                    """,
                    cancellationToken);
            }

            return snapshot.Count;
        }
#pragma warning disable CA1031 // A snapshot write must never take the host down; the next tick retries.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogWriteFailed(logger, exception);
            return 0;
        }
    }

    internal static string StateName(CircuitState state) => state switch
    {
        CircuitState.Closed => "closed",
        CircuitState.HalfOpen => "half_open",
        _ => "open",
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Writing the Provider Health snapshot failed; it will be retried.")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception);
}
