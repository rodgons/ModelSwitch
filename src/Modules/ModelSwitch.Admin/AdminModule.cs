using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ModelSwitch.Admin;

public static class AdminModule
{
    public static IServiceCollection AddAdminModule(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app) => app;
}
