using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelSwitch.Evaluation.Application;
using ModelSwitch.Evaluation.Endpoints;
using ModelSwitch.Evaluation.Infrastructure;
using ModelSwitch.SharedKernel.Hosting;
using ModelSwitch.SharedKernel.Persistence;

namespace ModelSwitch.Evaluation;

public static class EvaluationModule
{
    private const string _appConnection = "ConnectionStrings:Postgres";
    private const string _migratorConnection = "ConnectionStrings:PostgresMigrator";

    /// <summary>
    /// Registers the Dataset and Prompt services over the app-role connection (<c>ConnectionStrings:Postgres</c>). They need the
    /// Identity module's current Administrator, Tenant Grant checker, Tenant directory and audit writer.
    /// </summary>
    public static IServiceCollection AddEvaluationModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddEvaluationContext(_appConnection);
        services.TryAddSingleton<EvaluationAccess>();
        services.TryAddSingleton<DatasetService>();
        services.TryAddSingleton<PromptService>();
        return services;
    }

    /// <summary>Registers the evaluation schema migrator for the Migrator host (<c>ConnectionStrings:PostgresMigrator</c>).</summary>
    public static IServiceCollection AddEvaluationMigrations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddEvaluationContext(_migratorConnection);
        services.AddSingleton<IModuleMigrator, EvaluationMigrator>();
        return services;
    }

    public static IEndpointRouteBuilder MapEvaluationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return EvaluationEndpoints.Map(app);
    }

    private static void AddEvaluationContext(this IServiceCollection services, string connectionKey)
    {
        services.RequireSetting(connectionKey);
        services.AddDbContextFactory<EvaluationDbContext>((provider, options) =>
            options.UseNpgsql(
                provider.GetRequiredService<IConfiguration>()[connectionKey],
                npgsql => npgsql.MigrationsHistoryTable(EvaluationDbContext.MigrationsHistoryTable, EvaluationDbContext.Schema)));
    }
}
