using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelSwitch.Api;
using Shouldly;

namespace ModelSwitch.UnitTests.Api;

/// <summary>
/// Regression: behind the Compose proxy every admin request arrived as plain HTTP, so the antiforgery system (Secure cookie
/// policy) threw and GET /admin/antiforgery-token answered 500. The proxy's X-Forwarded-Proto must be honoured from the
/// Compose network, and only from private networks.
/// </summary>
public sealed class ProxyForwardedHeadersTests
{
    [Theory]
    [InlineData("172.18.0.7")]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.65.1")]
    [InlineData("127.0.0.1")]
    [InlineData("::ffff:172.18.0.7")]
    public async Task A_private_network_proxy_sets_the_scheme_and_client_address(string proxy)
    {
        var context = await ForwardAsync(proxy);

        context.Request.Scheme.ShouldBe("https");
        context.Request.IsHttps.ShouldBeTrue();
        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse("198.51.100.20"));
    }

    [Theory]
    [InlineData("203.0.113.9")]
    [InlineData("8.8.8.8")]
    public async Task A_public_peer_cannot_claim_https(string peer)
    {
        var context = await ForwardAsync(peer);

        context.Request.Scheme.ShouldBe("http");
        context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse(peer));
    }

    private static async Task<HttpContext> ForwardAsync(string peer)
    {
        using var services = new ServiceCollection().AddProxyForwardedHeaders().BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<ForwardedHeadersOptions>>();
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.20";

        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, options);
        await middleware.Invoke(context);
        return context;
    }
}
