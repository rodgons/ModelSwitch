#pragma warning disable CS1573 // Record parameters are documented only where they need explanation.

namespace ModelSwitch.Registry.Contracts;

/// <summary>The Price in force for a Provider Model, USD per million tokens.</summary>
/// <param name="CachedInputPerMillion">Null: no discount, cached tokens cost the input rate.</param>
public sealed record CatalogPrice(decimal InputPerMillion, decimal? CachedInputPerMillion, decimal OutputPerMillion);

/// <summary>One Provider Model with its Provider's state and Price in force. Contains no secret.</summary>
/// <param name="CapabilitiesJson">The <c>registry.provider_model.capabilities</c> document, as stored.</param>
/// <param name="Price">Null when no Price is in force yet.</param>
/// <param name="Target">What the attempt loop needs to call the model (endpoint, Adapter type, Credential Reference, timeout). Null only in tests.</param>
public sealed record CatalogEntry(
    Guid ProviderModelId,
    Guid ProviderId,
    string ProviderName,
    string ProviderModelIdentifier,
    bool ProviderEnabled,
    bool ModelEnabled,
    string CapabilitiesJson,
    CatalogPrice? Price,
    ProviderModelTarget? Target = null);

/// <summary>Providers, Provider Models, Capabilities and current Prices, as read for one configuration snapshot.</summary>
public sealed record CatalogSnapshot(IReadOnlyList<CatalogEntry> Entries);

/// <summary>Bulk read of the whole catalog for the runtime configuration snapshot (architecture.md section 6).</summary>
public interface ICatalogSnapshotSource
{
    Task<CatalogSnapshot> LoadAsync(CancellationToken cancellationToken);
}
