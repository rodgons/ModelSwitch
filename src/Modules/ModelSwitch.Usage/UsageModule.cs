using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ModelSwitch.Usage;

public static class UsageModule
{
    public static IServiceCollection AddUsageModule(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapUsageEndpoints(this IEndpointRouteBuilder app) => app;
}
