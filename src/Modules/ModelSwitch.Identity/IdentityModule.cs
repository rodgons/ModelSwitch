using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ModelSwitch.Identity;

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app) => app;
}
