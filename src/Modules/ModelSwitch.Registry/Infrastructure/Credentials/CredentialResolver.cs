using ModelSwitch.Registry.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Registry.Infrastructure.Credentials;

/// <summary>Dispatches a Credential Reference to the <see cref="ICredentialSource"/> for its scheme.</summary>
internal sealed class CredentialResolver(IEnumerable<ICredentialSource> sources) : ICredentialResolver
{
    private readonly Dictionary<string, ICredentialSource> _sources = sources.ToDictionary(s => s.Scheme, StringComparer.Ordinal);

    public async ValueTask<Result<CredentialSecret?>> ResolveAsync(string? credentialReference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentialReference))
        {
            return Result.Success<CredentialSecret?>(null);
        }

        var separator = credentialReference.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || !_sources.TryGetValue(credentialReference[..separator], out var source))
        {
            return new Error(ErrorCode.ProviderUnavailable, "Credential reference has an unsupported scheme.");
        }

        var resolved = await source.ResolveAsync(credentialReference[(separator + 1)..], cancellationToken).ConfigureAwait(false);
        return resolved.IsSuccess ? Result.Success<CredentialSecret?>(resolved.Value) : resolved.Error;
    }
}
