using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Registry.Infrastructure;
using ModelSwitch.Registry.Infrastructure.Credentials;
using ModelSwitch.Registry.Infrastructure.Outbound;
using ModelSwitch.SharedKernel.Hosting;
using ModelSwitch.SharedKernel.Persistence;

namespace ModelSwitch.Registry;

public static class RegistryModule
{
    private const string _migratorConnection = "ConnectionStrings:PostgresMigrator";

    public static IServiceCollection AddRegistryModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IEnvironmentReader, ProcessEnvironmentReader>();
        services.TryAddSingleton<IDnsResolver, SystemDnsResolver>();
        services.TryAddSingleton(provider => OutboundOptions.From(provider.GetRequiredService<IConfiguration>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICredentialSource, EnvCredentialSource>());
        // Phase 3: register a db: ICredentialSource (AES-GCM secret store) alongside the env one.
        services.TryAddSingleton<ICredentialResolver, CredentialResolver>();
        services.TryAddSingleton<IOutboundDestinationPolicy, OutboundDestinationPolicy>();
        services.TryAddSingleton<IOutboundHandlerFactory, OutboundHandlerFactory>();
        return services;
    }

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
