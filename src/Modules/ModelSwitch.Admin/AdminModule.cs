using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelSwitch.Admin.Application;
using ModelSwitch.Admin.Endpoints;

namespace ModelSwitch.Admin;

/// <summary>The dashboard's read models (architecture.md section 4): no tables of its own, only the other modules' query contracts.</summary>
public static class AdminModule
{
    public static IServiceCollection AddAdminModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<DashboardService>();
        return services;
    }

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return DashboardEndpoints.Map(app);
    }
}
