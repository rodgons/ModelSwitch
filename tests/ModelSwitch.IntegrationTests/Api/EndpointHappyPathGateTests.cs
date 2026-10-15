using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.IntegrationTests.Infrastructure;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>The endpoint gate (testing.md section 6): every route of the app has a test tagged <see cref="HappyPathAttribute"/>.</summary>
public sealed class EndpointHappyPathGateTests(GatewayFixture gateway) : IClassFixture<GatewayFixture>
{
    [Fact]
    public void Every_route_has_a_happy_path_test()
    {
        var routes = gateway.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => $"{method} {Normalize(endpoint.RoutePattern)}"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tagged = typeof(HappyPathAttribute).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .SelectMany(method => method.GetCustomAttributes<HappyPathAttribute>())
            .Select(attribute => attribute.Route)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        routes.ShouldNotBeEmpty();
        routes.Except(tagged, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty("routes without a [HappyPath] integration test");
        tagged.Except(routes, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty("[HappyPath] tags that match no route");
    }

    private static string Normalize(RoutePattern pattern) => pattern.RawText ?? string.Empty;
}
