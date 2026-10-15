using ModelSwitch.SharedKernel;

namespace ModelSwitch.Registry.Contracts;

/// <summary>Turns a Provider's Credential Reference (<c>env:NAME</c>, later <c>db:&lt;uuid&gt;</c>) into its secret.</summary>
public interface ICredentialResolver
{
    /// <summary>
    /// Resolves <paramref name="credentialReference"/>. A null or empty reference means the Provider needs no credential
    /// and succeeds with a null secret. An unknown scheme, malformed reference or unset variable fails with
    /// <c>provider_unavailable</c> (no Attempt can be made); the message names the reference, never a secret.
    /// </summary>
    ValueTask<Result<CredentialSecret?>> ResolveAsync(string? credentialReference, CancellationToken cancellationToken);
}
