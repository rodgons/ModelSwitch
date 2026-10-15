using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.Registry.Infrastructure;
using ModelSwitch.SharedKernel.Hosting;
using ModelSwitch.SharedKernel.Persistence;

namespace ModelSwitch.Registry;

public static class RegistryModule
{
    private const string _migratorConnection = "ConnectionStrings:PostgresMigrator";

    public static IServiceCollection AddRegistryModule(this IServiceCollection services) => services;

    /// <summary>Registers the registry schema migrator for the Migrator host (<c>ConnectionStrings:PostgresMigrator</c>).</summary>
    public static IServiceCollection AddRegistryMigrations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.RequireSetting(_migratorConnection);
        services.AddDbContextFactory<RegistryDbContext>((provider, options) =>
            options.UseNpgsql(
                provider.GetRequiredService<IConfiguration>()[_migratorConnection],
                npgsql => npgsql.MigrationsHistoryTable(RegistryDbContext.MigrationsHistoryTable, RegistryDbContext.Schema)));
        services.AddSingleton<IModuleMigrator, RegistryMigrator>();
        return services;
    }

    public static IEndpointRouteBuilder MapRegistryEndpoints(this IEndpointRouteBuilder app) => app;
}
