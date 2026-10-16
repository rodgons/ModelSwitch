namespace ModelSwitch.Registry.Contracts;

/// <summary>
/// One api instance's last written view of one Provider Model (data-model.md, <c>provider_health_snapshot</c>). <c>CircuitState</c> is
/// <c>closed</c>, <c>half_open</c> or <c>open</c>.
/// </summary>
public sealed record ProviderHealthSnapshotRow(string InstanceId, Guid ProviderModelId, string CircuitState, double WindowFailureRate, int WindowCalls, DateTimeOffset CapturedAt);

/// <summary>Reads the Provider Health every api instance writes (ADR-0005), for the dashboard's aggregate and per-instance view.</summary>
public interface IProviderHealthSnapshots
{
    Task<IReadOnlyList<ProviderHealthSnapshotRow>> ListAsync(CancellationToken cancellationToken);
}
