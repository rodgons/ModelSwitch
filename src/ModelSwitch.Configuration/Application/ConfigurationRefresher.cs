using Microsoft.Extensions.Logging;

namespace ModelSwitch.Configuration.Application;

internal interface IConfigurationVersionReader
{
    Task<long> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Rebuilds the snapshot when <c>config.config_version</c> moved past the current one, and swaps it in atomically.
/// Called on startup, on every <c>NOTIFY</c> and on the 5 s poll. A failed rebuild keeps the previous snapshot.
/// </summary>
internal sealed partial class ConfigurationRefresher(
    IConfigurationVersionReader versions,
    ISnapshotBuilder builder,
    SnapshotStore store,
    ILogger<ConfigurationRefresher> logger)
{
    // The loaders read module by module, so a write could land between two reads. The version is read again after the
    // build: if it moved, the build may mix versions and is repeated.
    private const int _maxConsistentReads = 3;

    /// <summary>Returns true when a newer snapshot was swapped in.</summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var seen = await versions.ReadAsync(cancellationToken);
            if (store.Version is { } current && current >= seen)
            {
                return false;
            }

            for (var attempt = 1; ; attempt++)
            {
                var snapshot = await builder.BuildAsync(seen, cancellationToken);
                var after = await versions.ReadAsync(cancellationToken);
                if (after == seen || attempt == _maxConsistentReads)
                {
                    // Stamped with the version read before the build, so a write during the build is picked up by the next poll.
                    var swapped = store.TrySwap(snapshot);
                    if (swapped)
                    {
                        LogSwapped(logger, snapshot.Version);
                    }

                    return swapped;
                }

                seen = after;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogRefreshFailed(logger, ex);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Configuration snapshot swapped to version {Version}.")]
    private static partial void LogSwapped(ILogger logger, long version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Configuration snapshot refresh failed; keeping the previous snapshot.")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception);
}
