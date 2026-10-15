using ModelSwitch.Configuration.Application;
using ModelSwitch.Configuration.Contracts;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Routing.Contracts;

namespace ModelSwitch.UnitTests.Configuration;

internal sealed class FakeVersionReader : IConfigurationVersionReader
{
    private readonly Queue<long> _scripted = new();

    public long Version { get; set; }

    public int Reads { get; private set; }

    /// <summary>The next reads return these values in order; afterwards <see cref="Version"/>.</summary>
    public void Script(params long[] versions)
    {
        foreach (var version in versions)
        {
            _scripted.Enqueue(version);
        }
    }

    public Task<long> ReadAsync(CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult(_scripted.Count > 0 ? _scripted.Dequeue() : Version);
    }
}

internal sealed class FakeSnapshotBuilder : ISnapshotBuilder
{
    public List<long> Built { get; } = [];

    public Exception? Failure { get; set; }

    public Action<long>? OnBuild { get; set; }

    public Task<ConfigurationSnapshot> BuildAsync(long version, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            throw failure;
        }

        Built.Add(version);
        OnBuild?.Invoke(version);
        return Task.FromResult(Snapshot(version));
    }

    public static ConfigurationSnapshot Snapshot(long version) => new(
        version,
        DateTimeOffset.UnixEpoch,
        new RoutingSnapshot(new Dictionary<string, Guid>(), new Dictionary<Guid, PolicySnapshot>(), new Dictionary<Guid, CandidateModel>()),
        new Dictionary<Guid, TenantLimits>());
}
