using ModelSwitch.Configuration.Contracts;

namespace ModelSwitch.Configuration.Application;

/// <summary>Holds the current snapshot. A swap is one reference write, so readers see the old or the new snapshot, never a mix.</summary>
internal sealed class SnapshotStore : IConfigurationSnapshotProvider
{
    private ConfigurationSnapshot? _current;

    public bool IsLoaded => Volatile.Read(ref _current) is not null;

    public ConfigurationSnapshot Current =>
        Volatile.Read(ref _current) ?? throw new InvalidOperationException("The configuration snapshot has not been loaded yet.");

    /// <summary>The version of the current snapshot, or null before the first load.</summary>
    public long? Version => Volatile.Read(ref _current)?.Version;

    /// <summary>
    /// Replaces the current snapshot when <paramref name="next"/> is newer. A stale build (a slower refresh that lost a
    /// race) never moves the version backwards. Returns whether the swap happened.
    /// </summary>
    public bool TrySwap(ConfigurationSnapshot next)
    {
        ArgumentNullException.ThrowIfNull(next);
        while (true)
        {
            var current = Volatile.Read(ref _current);
            if (current is not null && current.Version >= next.Version)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _current, next, current) == current)
            {
                return true;
            }
        }
    }
}
