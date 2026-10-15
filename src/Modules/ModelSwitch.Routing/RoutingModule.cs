using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ModelSwitch.Routing;

public static class RoutingModule
{
    public static IServiceCollection AddRoutingModule(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapRoutingEndpoints(this IEndpointRouteBuilder app) => app;
}
