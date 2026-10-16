using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel.Hosting;
using ModelSwitch.SharedKernel.Jobs;
using ModelSwitch.SharedKernel.Persistence;
using ModelSwitch.SharedKernel.Retention;
using ModelSwitch.Usage.Contracts;
using ModelSwitch.Usage.Domain;
using ModelSwitch.Usage.Infrastructure;

namespace ModelSwitch.Usage;

public static class UsageModule
{
    private const string _appConnection = "ConnectionStrings:Postgres";
    private const string _migratorConnection = "ConnectionStrings:PostgresMigrator";

    /// <summary>
    /// Registers the cost and token estimators (pure, no database) and the <see cref="IUsageLedger"/> over the app-role
    /// connection (<c>ConnectionStrings:Postgres</c>).
    /// </summary>
    public static IServiceCollection AddUsageModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ICostCalculator, CostCalculator>();
        services.TryAddSingleton<IInputTokenEstimator, InputTokenEstimator>();
        services.AddUsageContext(_appConnection);
        services.AddMetrics();
        services.TryAddSingleton<UsageMetrics>();
        services.TryAddSingleton<IUsageLedger, EfUsageLedger>();
        services.TryAddSingleton<IBudgets, EfBudgets>();
        services.TryAddSingleton<IUsageQueries, UsageQueries>();
        services.TryAddSingleton<ITenantBudgets, TenantBudgetsAdapter>();
        services.TryAddSingleton<IReservationSweeper, EfReservationSweeper>();
        services.AddJobHandler<ReservationSweepJob>();
        services.AddRecurringJob(ReservationSweepJob.JobKind, ReservationSweepJob.Interval);
        services.AddOutboxConsumer<UsageRollupConsumer>();
        services.AddRetentionTask<UsageRetentionTask>();
        return services;
    }

    /// <summary>Registers the usage schema migrator for the Migrator host (<c>ConnectionStrings:PostgresMigrator</c>).</summary>
    public static IServiceCollection AddUsageMigrations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddUsageContext(_migratorConnection);
        services.AddSingleton<IModuleMigrator, UsageMigrator>();
        return services;
    }

    public static IEndpointRouteBuilder MapUsageEndpoints(this IEndpointRouteBuilder app) => app;

    private static void AddUsageContext(this IServiceCollection services, string connectionKey)
    {
        services.RequireSetting(connectionKey);
        services.AddDbContextFactory<UsageDbContext>((provider, options) =>
            options.UseNpgsql(
                provider.GetRequiredService<IConfiguration>()[connectionKey],
                npgsql => npgsql.MigrationsHistoryTable(UsageDbContext.MigrationsHistoryTable, UsageDbContext.Schema)));
    }
}
