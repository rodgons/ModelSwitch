using System.Net;

namespace ModelSwitch.Registry.Infrastructure.Outbound;

/// <summary>Resolves a host name to addresses. Injectable so tests can simulate DNS rebinding.</summary>
internal interface IDnsResolver
{
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

internal sealed class SystemDnsResolver : IDnsResolver
{
    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        => await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
}
