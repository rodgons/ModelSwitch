using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ModelSwitch.Registry;

public static class RegistryModule
{
    public static IServiceCollection AddRegistryModule(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapRegistryEndpoints(this IEndpointRouteBuilder app) => app;
}
