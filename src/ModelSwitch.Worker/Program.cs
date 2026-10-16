using ModelSwitch.Evaluation;
using ModelSwitch.Identity;
using ModelSwitch.Inference;
using ModelSwitch.Registry;
using ModelSwitch.Routing;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Hosting;
using ModelSwitch.SharedKernel.Jobs;
using ModelSwitch.SharedKernel.Retention;
using ModelSwitch.Telemetry;
using ModelSwitch.Usage;
using Npgsql;

// Invalid deployment configuration exits with code 78 and names the variable (FR-10).
return await StartupExit.RunAsync(
    async () =>
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.AddEnvironmentVariables(RequiredSetting.EnvironmentPrefix);

        builder.AddModelSwitchTelemetry("modelswitch-worker");
        builder.Services.AddSharedKernel();
        builder.Services.AddUsageModule();
        builder.Services.AddEvaluationModule();
        builder.Services.AddRegistryModule();
        builder.Services.AddInferenceModule();
        builder.Services.AddIdentityModule();
        builder.Services.AddRoutingModule();

        // Jobs infrastructure: claim loops, recurring-job scheduler and outbox relay. Modules contribute handlers, schedules and
        // outbox consumers from their own registration (Usage adds the Reservation sweeper and the usage rollup).
        builder.Services.AddJobEnqueuer(provider => NpgsqlDataSource.Create(provider.GetRequiredService<IConfiguration>()["ConnectionStrings:Postgres"]!));
        builder.Services.AddJobsWorker();

        // Retention runs as modelswitch_retention when its connection string is set, otherwise as the app role (audit records kept).
        builder.Services.AddRetention(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var retention = configuration["ConnectionStrings:PostgresRetention"];
            return new RetentionDataSource(
                NpgsqlDataSource.Create(string.IsNullOrWhiteSpace(retention) ? configuration["ConnectionStrings:Postgres"]! : retention),
                IsRetentionRole: !string.IsNullOrWhiteSpace(retention));
        });

        await builder.Build().RunAsync();
        return 0;
    },
    Console.Error);
