using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ModelSwitch.SharedKernel.Jobs;

/// <summary>Runs <see cref="JobsOptions.Concurrency"/> claim loops over a <see cref="JobProcessor"/>.</summary>
internal sealed partial class JobWorkerService(JobProcessor processor, TimeProvider time, IOptions<JobsOptions> options, ILogger<JobWorkerService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.WhenAll(Enumerable.Range(0, Math.Max(1, options.Value.Concurrency)).Select(_ => Task.Run(() => LoopAsync(stoppingToken), CancellationToken.None)));

    private async Task LoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await processor.ProcessOneAsync(stoppingToken))
                {
                    await Task.Delay(options.Value.PollInterval, time, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogLoopFailed(logger, exception);
                await DelayQuietlyAsync(options.Value.PollInterval, time, stoppingToken);
            }
        }
    }

    internal static async Task DelayQuietlyAsync(TimeSpan delay, TimeProvider time, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, time, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The job worker loop failed; it will retry.")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception);
}

/// <summary>Enqueues each recurring job's slot once per poll interval.</summary>
internal sealed partial class RecurringJobService(RecurringJobScheduler scheduler, TimeProvider time, IOptions<JobsOptions> options, ILogger<RecurringJobService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await scheduler.ScheduleDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogFailed(logger, exception);
            }

            await JobWorkerService.DelayQuietlyAsync(options.Value.PollInterval, time, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduling recurring jobs failed; it will retry.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

/// <summary>Relays outbox events in batches, then waits when the outbox is drained.</summary>
internal sealed partial class OutboxRelayService(OutboxRelay relay, TimeProvider time, IOptions<JobsOptions> options, ILogger<OutboxRelayService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var relayed = 0;
            try
            {
                relayed = await relay.RelayAsync(options.Value.OutboxBatchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogFailed(logger, exception);
            }

            if (relayed == 0)
            {
                await JobWorkerService.DelayQuietlyAsync(options.Value.PollInterval, time, stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The outbox relay failed; it will retry.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
