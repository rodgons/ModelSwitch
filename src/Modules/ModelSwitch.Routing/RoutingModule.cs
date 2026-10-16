using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelSwitch.Routing.Application;
using ModelSwitch.Routing.Contracts;
using ModelSwitch.Routing.Endpoints;
using ModelSwitch.Routing.Infrastructure;
using ModelSwitch.SharedKernel.Hosting;
using ModelSwitch.SharedKernel.Persistence;

namespace ModelSwitch.Routing;

public static class RoutingModule
{
    private const string _appConnection = "ConnectionStrings:Postgres";
    private const string _migratorConnection = "ConnectionStrings:PostgresMigrator";

    public static IServiceCollection AddRoutingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<Contracts.IRouter, Router>();
        services.TryAddSingleton<IPolicyDefinitionValidator, PolicyDefinitionValidator>();
        services.AddRoutingContext(_appConnection);
        services.TryAddSingleton<PolicyAdminService>();
        services.TryAddSingleton<AliasAdminService>();
        services.TryAddSingleton<EfPolicyStore>();
        services.TryAddSingleton<IPolicyStore>(provider => provider.GetRequiredService<EfPolicyStore>());
        services.TryAddSingleton<IPolicyCatalogSource>(provider => provider.GetRequiredService<EfPolicyStore>());
        return services;
    }

    /// <summary>Registers the routing schema migrator for the Migrator host (<c>ConnectionStrings:PostgresMigrator</c>).</summary>
    public static IServiceCollection AddRoutingMigrations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddRoutingContext(_migratorConnection);
        services.AddSingleton<IModuleMigrator, RoutingMigrator>();
        return services;
    }

    public static IEndpointRouteBuilder MapRoutingEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return RoutingAdminEndpoints.Map(app);
    }

    private static void AddRoutingContext(this IServiceCollection services, string connectionKey)
    {
        services.RequireSetting(connectionKey);
        services.AddDbContextFactory<RoutingDbContext>((provider, options) =>
            options.UseNpgsql(
                provider.GetRequiredService<IConfiguration>()[connectionKey],
                npgsql => npgsql.MigrationsHistoryTable(RoutingDbContext.MigrationsHistoryTable, RoutingDbContext.Schema)));
    }
}
