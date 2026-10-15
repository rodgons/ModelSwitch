using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Registry.Infrastructure;
using ModelSwitch.Registry.Infrastructure.Credentials;
using ModelSwitch.Registry.Infrastructure.Health;
using ModelSwitch.Registry.Infrastructure.Outbound;
using ModelSwitch.SharedKernel.Hosting;
using ModelSwitch.SharedKernel.Persistence;

namespace ModelSwitch.Registry;

public static class RegistryModule
{
    private const string _appConnection = "ConnectionStrings:Postgres";
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
        services.RequireSetting(_appConnection);
        services.AddDbContextFactory<RegistryDbContext>((provider, options) =>
            options.UseNpgsql(
                provider.GetRequiredService<IConfiguration>()[_appConnection],
                npgsql => npgsql.MigrationsHistoryTable(RegistryDbContext.MigrationsHistoryTable, RegistryDbContext.Schema)));
        services.TryAddSingleton<IProviderCatalog, EfProviderCatalog>();
        services.TryAddSingleton<IAdapterFactory, AdapterFactory>();
        services.TryAddSingleton(provider => CircuitOptions.From(provider.GetRequiredService<IConfiguration>()));
        services.TryAddSingleton<IProviderHealth, ProviderHealth>();
        return services;
    }

    /// <summary>
    /// Runs the job that writes this instance's Provider Health to <c>registry.provider_health_snapshot</c> every 15 s.
    /// Add it to the host that serves inference (the api), because the circuit breakers live in that process.
    /// </summary>
    public static IServiceCollection AddProviderHealthSnapshots(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<ProviderHealthSnapshotService>();
        return services;
    }

    /// <summary>
    /// Registers an Adapter implementation for Providers whose type is <paramref name="typeName"/>. Its
    /// <c>HttpClient</c> uses the outbound destination policy handler, has no timeout of its own (the Adapter applies
    /// the Attempt timeout) and no resilience handlers (ADR-0004).
    /// </summary>
    public static IServiceCollection AddProviderAdapter<TAdapter>(this IServiceCollection services, string typeName)
        where TAdapter : class, IProviderAdapter
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        services.AddHttpClient<TAdapter>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(provider => provider.GetRequiredService<IOutboundHandlerFactory>().CreatePrimaryHandler());
        services.AddSingleton(new AdapterRegistration(typeName, typeof(TAdapter)));
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
