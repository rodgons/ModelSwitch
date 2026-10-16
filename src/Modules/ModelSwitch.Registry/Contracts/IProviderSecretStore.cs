using ModelSwitch.SharedKernel;

namespace ModelSwitch.Registry.Contracts;

/// <summary>
/// What an administrator may learn about a Provider's credential. Deliberately has no member that carries the secret or
/// a fragment of it: secrets are write-only (ADR-0007, api.md section 8).
/// </summary>
/// <param name="Kind"><c>none</c>, <c>env</c> or <c>db</c>.</param>
/// <param name="Configured">True when the Provider has a Credential Reference.</param>
/// <param name="UpdatedAt">When a <c>db</c> secret was last set; null for other kinds.</param>
public sealed record ProviderSecretStatus(string Kind, bool Configured, DateTimeOffset? UpdatedAt);

/// <summary>
/// The write-only side of Provider credentials stored in the database (<c>db:</c> Credential References, encrypted at
/// rest under the master key). The admin endpoints call this; nothing here ever returns the secret.
/// </summary>
public interface IProviderSecretStore
{
    /// <summary>
    /// Sets or replaces the Provider's secret: encrypts it, points the Provider's Credential Reference at the new
    /// <c>db:</c> row, removes the previous one and bumps the config version, in one transaction. Fails with
    /// <c>not_found</c> (no such Provider), <c>validation_failed</c> (empty or oversized secret) or
    /// <c>internal_error</c> (master key not configured).
    /// </summary>
    Task<Result<ProviderSecretStatus>> SetAsync(Guid providerId, string secret, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the Provider's <c>db:</c> secret and its Credential Reference. Idempotent; a Provider with no <c>db:</c>
    /// reference is left unchanged. Fails with <c>not_found</c> for an unknown Provider.
    /// </summary>
    Task<Result<ProviderSecretStatus>> ClearAsync(Guid providerId, CancellationToken cancellationToken);

    /// <summary>Reports whether the Provider has a credential and of which kind. Fails with <c>not_found</c> for an unknown Provider.</summary>
    Task<Result<ProviderSecretStatus>> GetStatusAsync(Guid providerId, CancellationToken cancellationToken);

    /// <summary>
    /// Re-encrypts every stored secret under the current master key version (after a master key rotation, ADR-0007).
    /// Returns how many rows were rewritten. Fails with <c>internal_error</c> when the master key is not configured or
    /// a stored secret cannot be decrypted.
    /// </summary>
    Task<Result<int>> ReencryptAllAsync(CancellationToken cancellationToken);
}
