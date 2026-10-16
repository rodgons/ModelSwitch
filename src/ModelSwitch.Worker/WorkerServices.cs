using ModelSwitch.Adapters.Ollama;
using ModelSwitch.Adapters.OpenAICompatible;
using ModelSwitch.Configuration;
using ModelSwitch.Evaluation;
using ModelSwitch.Identity;
using ModelSwitch.Inference;
using ModelSwitch.Registry;
using ModelSwitch.Routing;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Jobs;
using ModelSwitch.SharedKernel.Retention;
using ModelSwitch.Usage;
using Npgsql;

namespace ModelSwitch.Worker;

/// <summary>
/// The worker host's services (architecture.md section 2), apart from telemetry: the modules, the jobs infrastructure and retention.
/// The worker runs Evaluation Runs, so it also has what <c>IInferenceService</c> needs, as the api host does: the configuration
/// snapshot (kept current by LISTEN/NOTIFY and the 5 s poll), the Provider Adapters, and this instance's Provider Health snapshots.
/// Circuit breakers are per instance (ADR-0005).
/// </summary>
public static class WorkerServices
{
    public static IServiceCollection AddWorkerServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSharedKernel();
        services.AddUsageModule();
        services.AddEvaluationModule();
        services.AddRegistryModule();
        services.AddProviderHealthSnapshots();
        services.AddProviderAdapter<OllamaAdapter>(OllamaAdapter.TypeName);
        services.AddProviderAdapter<OpenAICompatibleAdapter>(OpenAICompatibleAdapter.TypeName);
        services.AddInferenceModule();
        services.AddIdentityModule();
        services.AddRoutingModule();
        services.AddConfigurationSnapshot();

        // Jobs infrastructure: claim loops, recurring-job scheduler and outbox relay. Modules contribute handlers, schedules and
        // outbox consumers from their own registration (Usage adds the Reservation sweeper and the usage rollup, Evaluation the
        // evaluation.run handler).
        services.AddJobEnqueuer(provider => NpgsqlDataSource.Create(provider.GetRequiredService<IConfiguration>()["ConnectionStrings:Postgres"]!));
        services.AddJobsWorker();

        // Retention runs as modelswitch_retention when its connection string is set, otherwise as the app role (audit records kept).
        services.AddRetention(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var retention = configuration["ConnectionStrings:PostgresRetention"];
            return new RetentionDataSource(
                NpgsqlDataSource.Create(string.IsNullOrWhiteSpace(retention) ? configuration["ConnectionStrings:Postgres"]! : retention),
                IsRetentionRole: !string.IsNullOrWhiteSpace(retention));
        });
        return services;
    }
}
