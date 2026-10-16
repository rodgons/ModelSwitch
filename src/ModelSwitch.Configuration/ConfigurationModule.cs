using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ModelSwitch.Configuration.Application;
using ModelSwitch.Configuration.Contracts;
using ModelSwitch.Configuration.Infrastructure;
using ModelSwitch.SharedKernel.Hosting;
using ModelSwitch.SharedKernel.Http;
using ModelSwitch.SharedKernel.Persistence;

namespace ModelSwitch.Configuration;

/// <summary>
/// Owns the <c>config</c> schema (<c>config.config_version</c>) and the runtime configuration snapshot (architecture.md
/// section 6). It is infrastructure that composes the other modules' Contracts loaders, not a domain module: it has no
/// endpoints and no business rules. The write side is <see cref="SharedKernel.Configuration.ConfigVersion"/>.
/// </summary>
public static class ConfigurationModule
{
    private const string _appConnection = "ConnectionStrings:Postgres";
    private const string _migratorConnection = "ConnectionStrings:PostgresMigrator";

    /// <summary>
    /// Registers <see cref="IConfigurationSnapshotProvider"/>, the LISTEN connection, the 5 s poll and the readiness check.
    /// Requires the Registry, Routing and Identity modules (their snapshot loaders) and <c>ConnectionStrings:Postgres</c>.
    /// </summary>
    public static IServiceCollection AddConfigurationSnapshot(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.RequireSetting(_appConnection);
        services.TryAddSingleton<SnapshotStore>();
        services.TryAddSingleton<IConfigurationSnapshotProvider>(provider => provider.GetRequiredService<SnapshotStore>());
        services.TryAddSingleton<ISnapshotBuilder, SnapshotBuilder>();
        services.TryAddSingleton<IConfigurationVersionReader>(provider =>
            new NpgsqlVersionReader(provider.GetRequiredService<IConfiguration>()[_appConnection]!));
        services.TryAddSingleton<ConfigurationRefresher>();
        services.TryAddSingleton<ConfigChangeSignal>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ConfigurationRefreshService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ConfigChangeListener>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IReadinessCheck, ConfigurationReadinessCheck>());
        return services;
    }

    /// <summary>Registers the <c>config</c> schema migrator for the Migrator host (<c>ConnectionStrings:PostgresMigrator</c>).</summary>
    public static IServiceCollection AddConfigurationMigrations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.RequireSetting(_migratorConnection);
        services.AddSingleton<IModuleMigrator>(provider =>
            new ConfigurationMigrator(provider.GetRequiredService<IConfiguration>()[_migratorConnection]!));
        services.AddSingleton<IModuleMigrator>(provider =>
            new JobsSchemaMigrator(provider.GetRequiredService<IConfiguration>()[_migratorConnection]!));
        return services;
    }
}
