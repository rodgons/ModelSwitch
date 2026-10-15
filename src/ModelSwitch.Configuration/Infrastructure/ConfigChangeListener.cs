using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelSwitch.Configuration.Application;
using ModelSwitch.SharedKernel.Configuration;
using Npgsql;

namespace ModelSwitch.Configuration.Infrastructure;

/// <summary>
/// <c>LISTEN config_changed</c> on a dedicated connection (never a pooled one: the session must stay open). Each
/// notification raises the refresh signal. A lost connection is re-established with exponential backoff, and every
/// (re)connect raises the signal too, because notifications sent while disconnected are gone. The 5 s version poll
/// in <see cref="ConfigurationRefreshService"/> covers the same gap, so a failure here never blocks propagation.
/// </summary>
internal sealed partial class ConfigChangeListener(
    IConfiguration configuration,
    ConfigChangeSignal signal,
    TimeProvider time,
    ILogger<ConfigChangeListener> logger) : BackgroundService
{
    public static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = InitialBackoff;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(() => backoff = InitialBackoff, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogListenFailed(logger, ex, backoff);
            }

            try
            {
                await Task.Delay(backoff, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private async Task ListenAsync(Action connected, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(configuration["ConnectionStrings:Postgres"])
        {
            Pooling = false,
            KeepAlive = 30,
            Enlist = false,
        };

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        connection.Notification += (_, _) => signal.Raise();
        await connection.OpenAsync(cancellationToken);
        await using (var listen = new NpgsqlCommand($"LISTEN {ConfigVersion.Channel}", connection))
        {
            await listen.ExecuteNonQueryAsync(cancellationToken);
        }

        connected();
        LogListening(logger);
        signal.Raise(); // Anything published while this connection was down.

        while (true)
        {
            await connection.WaitAsync(cancellationToken); // Returns after each notification; throws when the connection drops.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening for configuration changes.")]
    private static partial void LogListening(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The configuration listener lost its connection; reconnecting in {Backoff}. The 5 s version poll keeps the snapshot current meanwhile.")]
    private static partial void LogListenFailed(ILogger logger, Exception exception, TimeSpan backoff);
}
