using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Registry.Infrastructure;

/// <summary>Maps an Adapter type name (<c>ollama</c>, <c>openai_compatible</c>) to the registered Adapter type.</summary>
internal sealed record AdapterRegistration(string TypeName, Type AdapterType);

internal sealed class AdapterFactory(
    IEnumerable<AdapterRegistration> registrations,
    IServiceProvider services,
    ICredentialResolver credentials) : IAdapterFactory
{
    private readonly Dictionary<string, Type> _byType = registrations.ToDictionary(r => r.TypeName, r => r.AdapterType, StringComparer.Ordinal);

    public async ValueTask<Result<AdapterBinding>> BindAsync(ProviderModelTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!_byType.TryGetValue(target.AdapterType, out var adapterType))
        {
            return new Error(ErrorCode.ProviderUnavailable, "The Provider's Adapter type is not available in this deployment.");
        }

        var secret = await credentials.ResolveAsync(target.CredentialReference, cancellationToken);
        if (secret.IsFailure)
        {
            return secret.Error;
        }

        // A typed HttpClient: each resolution takes a handler from IHttpClientFactory, which pools and rotates it.
        var adapter = (IProviderAdapter)services.GetRequiredService(adapterType);
        var context = new AdapterContext(target.Endpoint, secret.Value?.Reveal(), target.ProviderModelIdentifier, target.RequestTimeout);
        return new AdapterBinding(adapter, context);
    }
}
