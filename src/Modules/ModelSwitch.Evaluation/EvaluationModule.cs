using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelSwitch.Evaluation.Application;
using ModelSwitch.Evaluation.Contracts;
using ModelSwitch.Evaluation.Endpoints;
using ModelSwitch.Evaluation.Infrastructure;
using ModelSwitch.SharedKernel.Hosting;
using ModelSwitch.SharedKernel.Jobs;
using ModelSwitch.SharedKernel.Persistence;

namespace ModelSwitch.Evaluation;

public static class EvaluationModule
{
    private const string _appConnection = "ConnectionStrings:Postgres";
    private const string _migratorConnection = "ConnectionStrings:PostgresMigrator";

    /// <summary>
    /// Registers the Dataset, Prompt and Evaluation Run services, the <c>evaluation.run</c> job handler and the recurring
    /// <c>evaluation.sweep_orphaned_runs</c> job (app-role connection). They need the Identity module's current Administrator, Tenant
    /// Grant, Tenant directory and audit writer, IPolicyStore, IProviderCatalog, IInferenceService, IEvaluationRunCost and the host's
    /// IJobEnqueuer and IJobStatusReader.
    /// </summary>
    public static IServiceCollection AddEvaluationModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddEvaluationContext(_appConnection);
        services.TryAddSingleton<EvaluationAccess>();
        services.TryAddSingleton<DatasetService>();
        services.TryAddSingleton<PromptService>();
        services.TryAddSingleton<RunPlanner>();
        services.TryAddSingleton<CaseCosting>();
        services.TryAddSingleton<EvaluationRunService>();
        services.TryAddSingleton<EvaluationRunQueries>();
        services.TryAddSingleton<RunExporter>();
        services.TryAddSingleton<RunExecutor>();
        services.TryAddSingleton<IEvaluationCaseLinks, EvaluationCaseLinks>();
        services.AddJobHandler<EvaluationRunJob>();
        services.AddJobHandler<OrphanedRunSweepJob>();
        services.AddRecurringJob(OrphanedRunSweepJob.JobKind, OrphanedRunSweepJob.Interval);
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
