namespace ModelSwitch.Registry.Domain;

/// <summary>An immutable Price. The one in force is the row with the latest <see cref="EffectiveFrom"/> not after now.</summary>
internal sealed class ModelPrice
{
    public Guid Id { get; init; }

    public Guid ProviderModelId { get; init; }

    public decimal InputPerMillion { get; init; }

    /// <summary>Null means no discount: cached tokens cost the input rate. Zero means free.</summary>
    public decimal? CachedInputPerMillion { get; init; }

    public decimal OutputPerMillion { get; init; }

    public required string Currency { get; init; }

    public DateTimeOffset EffectiveFrom { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public Guid? CreatedByAdminId { get; init; }
}
