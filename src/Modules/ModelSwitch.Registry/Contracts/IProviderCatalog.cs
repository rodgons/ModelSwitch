namespace ModelSwitch.Registry.Contracts;

/// <summary>A Provider Model together with what is needed to call it. Contains no secret, only a Credential Reference.</summary>
public sealed record ProviderModelTarget(
    Guid ProviderModelId,
    Guid ProviderId,
    string ProviderName,
    string AdapterType,
    Uri Endpoint,
    string? CredentialReference,
    TimeSpan RequestTimeout,
    string ProviderModelIdentifier);

/// <summary>Read access to the Provider and Provider Model configuration.</summary>
public interface IProviderCatalog
{
    /// <summary>
    /// Finds an enabled Provider Model by the Provider's name and the Provider's own model identifier. Null when either
    /// is unknown or disabled. Phase 1 reads the database on every call; Phase 2 replaces this with the
    /// configuration snapshot (architecture.md section 6).
    /// </summary>
    Task<ProviderModelTarget?> FindEnabledAsync(string providerName, string providerModelIdentifier, CancellationToken cancellationToken);
}
