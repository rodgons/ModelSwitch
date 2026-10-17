using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace ModelSwitch.Api;

/// <summary>
/// The api runs behind the <c>proxy</c> service, which terminates TLS (docs/deployment.md section 5). The proxy's
/// <c>X-Forwarded-Proto</c> makes the request count as HTTPS, which the Secure session and antiforgery cookies require
/// (ADR-0011), and <c>X-Forwarded-For</c> gives sign-in lockout and Audit Records the client address instead of the proxy's.
/// The headers are honoured only from loopback and private-network peers (the Compose network), never from the Internet.
/// </summary>
internal static class ProxyForwardedHeaders
{
    private static readonly string[] _trustedNetworks = ["127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "::1/128", "fc00::/7"];

    public static IServiceCollection AddProxyForwardedHeaders(this IServiceCollection services)
        => services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var network in _trustedNetworks)
            {
                options.KnownIPNetworks.Add(IPNetwork.Parse(network));
            }
        });
}
