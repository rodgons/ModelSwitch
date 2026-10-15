using Microsoft.Extensions.Hosting;

namespace ModelSwitch.Configuration.Application;

/// <summary>
/// Keeps the snapshot current: the first load runs in <see cref="StartAsync"/> so the host is ready as soon as it
/// listens, then it refreshes on every <c>NOTIFY</c> signal and at least every <see cref="PollInterval"/> (the fallback
/// when a notification is lost).
/// </summary>
internal sealed class ConfigurationRefreshService(
    ConfigurationRefresher refresher,
    ConfigChangeSignal signal,
    TimeProvider time) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // A failure is logged and leaves the instance not ready; the loop below keeps retrying.
        await refresher.RefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var notified = signal.WaitAsync(wake.Token);
            var polled = Task.Delay(PollInterval, time, wake.Token);
            try
            {
                await Task.WhenAny(notified, polled);
            }
            finally
            {
                await wake.CancelAsync();
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            // A signal consumed by a cancelled wait is not lost: the refresh below runs either way.
            await refresher.RefreshAsync(stoppingToken);
        }
    }
}
