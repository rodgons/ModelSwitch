namespace ModelSwitch.Registry.Domain;

internal sealed class ProviderModel
{
    public Guid Id { get; init; }

    public Guid ProviderId { get; init; }

    /// <summary>The Provider's own model identifier, e.g. <c>gpt-4o-mini</c>.</summary>
    public required string ProviderModelIdentifier { get; init; }

    public required string DisplayName { get; set; }

    /// <summary>Capabilities as a JSON document (docs/data-model.md, <c>registry.provider_model</c>).</summary>
    public required string CapabilitiesJson { get; set; }

    public bool Enabled { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}
