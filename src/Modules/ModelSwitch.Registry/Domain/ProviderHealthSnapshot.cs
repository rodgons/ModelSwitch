namespace ModelSwitch.Registry.Domain;

/// <summary>One instance's view of one Provider Model's health, upserted periodically (data-model.md, <c>provider_health_snapshot</c>).</summary>
internal sealed class ProviderHealthSnapshot
{
    public required string InstanceId { get; init; }

    public Guid ProviderModelId { get; init; }

    /// <summary><c>closed</c>, <c>half_open</c> or <c>open</c>.</summary>
    public required string CircuitState { get; set; }

    public float WindowFailureRate { get; set; }

    public int WindowCalls { get; set; }

    public DateTimeOffset CapturedAt { get; set; }
}
