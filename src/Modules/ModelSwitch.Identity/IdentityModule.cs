using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ModelSwitch.Identity.Application;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Identity.Domain;
using ModelSwitch.Identity.Infrastructure;
using ModelSwitch.SharedKernel.Hosting;
using ModelSwitch.SharedKernel.Http;
using ModelSwitch.SharedKernel.Persistence;

namespace ModelSwitch.Identity;

public static class IdentityModule
{
    private const string _appConnection = "ConnectionStrings:Postgres";
    private const string _migratorConnection = "ConnectionStrings:PostgresMigrator";
    private const string _pepperKey = "Security:ApiKeyPepper";

    /// <summary>
    /// Registers API Key authentication for api/worker hosts: the <see cref="IApiKeyAuthenticator"/>, its 30 s cache
    /// and the app-role database access. Requires <c>ConnectionStrings:Postgres</c> and <c>Security:ApiKeyPepper</c>.
    /// </summary>
    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIdentityContext(_appConnection);
        services.AddApiKeyHasher();
        services.TryAddSingleton<IApiKeyStore, EfApiKeyStore>();
        services.TryAddSingleton<VerifiedKeyCache>();
        services.TryAddSingleton<ApiKeyUsageRecorder>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ApiKeyUsageWriter>());
        services.TryAddSingleton<IApiKeyAuthenticator, ApiKeyAuthenticator>();
        services.TryAddSingleton<ITenantLimitsSource, NoTenantLimitsSource>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IReadinessCheck, PostgresReadinessCheck>());
        return services;
    }

    /// <summary>Registers the identity schema migrator for the Migrator host (<c>ConnectionStrings:PostgresMigrator</c>).</summary>
    public static IServiceCollection AddIdentityMigrations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIdentityContext(_migratorConnection);
        services.AddSingleton<IModuleMigrator, IdentityMigrator>();
        return services;
    }

    /// <summary>Registers <see cref="IIdentitySeeder"/> for the Migrator host. Also requires <c>Security:ApiKeyPepper</c>.</summary>
    public static IServiceCollection AddIdentitySeeding(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddApiKeyHasher();
        services.TryAddSingleton<IIdentitySeeder, IdentitySeeder>();
        return services;
    }

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app) => app;

    private static void AddIdentityContext(this IServiceCollection services, string connectionKey)
    {
        services.RequireSetting(connectionKey);
        services.AddDbContextFactory<IdentityDbContext>((provider, options) =>
            options.UseNpgsql(
                provider.GetRequiredService<IConfiguration>()[connectionKey],
                npgsql => npgsql.MigrationsHistoryTable(IdentityDbContext.MigrationsHistoryTable, IdentityDbContext.Schema)));
    }

    private static void AddApiKeyHasher(this IServiceCollection services)
    {
        services.RequireSetting(
            _pepperKey,
            value => ApiKeyHasher.FromBase64(value) is null ? "it must be base64 and decode to at least 32 bytes (openssl rand -base64 32)." : null);
        services.TryAddSingleton(provider => ApiKeyHasher.FromBase64(provider.GetRequiredService<IConfiguration>()[_pepperKey]!)
            ?? throw new InvalidOperationException("The API key pepper was not validated at startup."));
    }
}
