using ModelSwitch.SharedKernel;

namespace ModelSwitch.Registry.Contracts;

/// <summary>The outbound destination policy for Provider endpoints (security.md section 5).</summary>
public interface IOutboundDestinationPolicy
{
    /// <summary>
    /// Validates a Provider endpoint on save: scheme, host, IP literals, and every address the host currently resolves to.
    /// Fails with <c>validation_failed</c> (field <c>endpoint</c>). The connect-time check still runs on every connection.
    /// </summary>
    ValueTask<Result> ValidateEndpointAsync(string endpoint, CancellationToken cancellationToken);
}
