using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.SharedKernel;

namespace ModelSwitch.Registry.Contracts;

/// <summary>An Adapter plus the call context for one Provider Model, with the credential already resolved.</summary>
public sealed record AdapterBinding(IProviderAdapter Adapter, AdapterContext Context);

/// <summary>
/// Creates the Adapter for a Provider's protocol and the <see cref="AdapterContext"/> for one call. Adapter
/// <c>HttpClient</c>s use the outbound destination policy handler and no resilience handlers (ADR-0004).
/// </summary>
public interface IAdapterFactory
{
    /// <summary>
    /// Fails with <c>provider_unavailable</c> when no Adapter is registered for the Provider's type or its
    /// Credential Reference cannot be resolved; no Attempt can be made in either case.
    /// </summary>
    ValueTask<Result<AdapterBinding>> BindAsync(ProviderModelTarget target, CancellationToken cancellationToken);
}
