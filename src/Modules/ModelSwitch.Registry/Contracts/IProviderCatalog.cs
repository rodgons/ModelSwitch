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
    string ProviderModelIdentifier,
    string? Tokenizer = null);

/// <summary>Read access to the Provider and Provider Model configuration.</summary>
public interface IProviderCatalog
{
    /// <summary>
    /// Finds an enabled Provider Model by the Provider's name and the Provider's own model identifier. Null when either
    /// is unknown or disabled. Reads the database on every call, so it is not for the request hot path: inference reads
    /// <see cref="ProviderModelTarget"/> from the configuration snapshot instead (architecture.md section 6).
    /// </summary>
    Task<ProviderModelTarget?> FindEnabledAsync(string providerName, string providerModelIdentifier, CancellationToken cancellationToken);

    /// <summary>
    /// Finds an enabled Provider Model (of an enabled Provider) by its id. Null when it is unknown or either is disabled. Reads the
    /// database: an Evaluation Run calls it once, at creation, to check a pinned <c>providerModelId</c> target.
    /// </summary>
    Task<ProviderModelTarget?> FindEnabledByIdAsync(Guid providerModelId, CancellationToken cancellationToken);
}
