using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModelSwitch.Identity.Application;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Identity.Domain;
using ModelSwitch.Identity.Endpoints;
using ModelSwitch.Identity.Infrastructure;
using ModelSwitch.SharedKernel.Hosting;
using ModelSwitch.SharedKernel.Http;
using ModelSwitch.SharedKernel.Persistence;
using ModelSwitch.SharedKernel.Retention;

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
        services.TryAddSingleton<ITenantLimitsSource, EfTenantLimitsSource>();
        services.TryAddSingleton<ITenantDirectory, EfTenantDirectory>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IReadinessCheck, PostgresReadinessCheck>());
        services.AddAdministratorServices();
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IAuditCallContext, HttpAuditCallContext>();
        services.TryAddSingleton<ICurrentAdministrator, HttpCurrentAdministrator>();
        services.TryAddSingleton<ITenantGrantChecker, TenantGrantChecker>();
        services.TryAddSingleton<IAdministratorSignIn, AdministratorSignInService>();
        services.TryAddSingleton<IAdministratorSessions, AdministratorSessionService>();
        services.TryAddSingleton<IAuditStore, EfAuditStore>();
        services.TryAddSingleton<AdministratorService>();
        services.TryAddSingleton<AuditQueryService>();
        services.TryAddSingleton<ITenantAdminStore, EfTenantAdminStore>();
        services.TryAddSingleton<IApiKeyAdminStore, EfApiKeyAdminStore>();
        services.TryAddSingleton<TenantService>();
        services.TryAddSingleton<ApiKeyAdminService>();
        services.AddRetentionTask<AuditRetentionTask>();
        return services;
    }

    /// <summary>
    /// Registers what Administrator cookie sessions need beyond <see cref="AddIdentityModule"/>: the Data Protection key ring in
    /// PostgreSQL (shared by every api instance) and the antiforgery cookie and header names (ADR-0011).
    /// </summary>
    public static IServiceCollection AddAdministratorWebSecurity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddDataProtection().SetApplicationName("ModelSwitch");
        services.TryAddSingleton<EfXmlRepository>();
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(provider => new ConfigureOptions<KeyManagementOptions>(
            options => options.XmlRepository = provider.GetRequiredService<EfXmlRepository>()));
        services.AddAntiforgery(options =>
        {
            options.HeaderName = AdminAuthentication.AntiforgeryHeaderName;
            options.Cookie.Name = AdminAuthentication.AntiforgeryCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.Path = "/";
        });
        return services;
    }

    /// <summary>Registers the identity schema migrator for the Migrator host (<c>ConnectionStrings:PostgresMigrator</c>).</summary>
    public static IServiceCollection AddIdentityMigrations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddIdentityContext(_migratorConnection);
        services.AddSingleton<IModuleMigrator, IdentityMigrator>();
        services.AddAdministratorServices();
        services.TryAddSingleton<IAuditCallContext, SystemAuditCallContext>();
        services.TryAddSingleton<IOwnerBootstrapper, OwnerBootstrapper>();
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

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        AdministratorEndpoints.Map(app);
        return TenantEndpoints.Map(app);
    }

    // Shared by the api host and the Migrator: the stores, the audit writer and the PBKDF2 password hasher.
    private static void AddAdministratorServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IPasswordHasher<Administrator>, PasswordHasher<Administrator>>();
        services.TryAddSingleton<IAdministratorStore, EfAdministratorStore>();
        services.TryAddSingleton<ISessionStore, EfSessionStore>();
        services.TryAddSingleton<IAuditWriter, AuditWriter>();
    }

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
