using ModelSwitch.Configuration.Contracts;
using ModelSwitch.SharedKernel.Http;

namespace ModelSwitch.Configuration.Infrastructure;

/// <summary>"Configuration snapshot loaded" for <c>GET /ready</c> (api.md section 4).</summary>
internal sealed class ConfigurationReadinessCheck(IConfigurationSnapshotProvider snapshots) : IReadinessCheck
{
    public string Name => "configuration";

    public Task<bool> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(snapshots.IsLoaded);
}
