using System.Net;
using System.Net.Sockets;
using ModelSwitch.Registry.Contracts;

namespace ModelSwitch.Registry.Infrastructure.Outbound;

internal sealed class OutboundHandlerFactory(OutboundOptions options, IDnsResolver resolver) : IOutboundHandlerFactory
{
    public HttpMessageHandler CreatePrimaryHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false, // a proxy would connect on our behalf and bypass the connect-time check
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = ConnectAsync,
    };

    internal async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        IReadOnlyList<IPAddress> addresses;
        if (options.IsAllowed(host))
        {
            addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
                ? [literal]
                : await resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Every resolved address is checked, so a rebinding host that returns a private address is refused here.
            var checkedAddresses = await OutboundDestinationPolicy.ResolveAndCheckAsync(host, resolver, cancellationToken).ConfigureAwait(false);
            if (checkedAddresses.IsFailure)
            {
                throw new HttpRequestException(HttpRequestError.ConnectionError, "Outbound destination refused by policy.");
            }

            addresses = checkedAddresses.Value;
        }

        SocketException? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                last = ex;
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException(HttpRequestError.ConnectionError, "Could not connect to the Provider.", last);
    }
}
