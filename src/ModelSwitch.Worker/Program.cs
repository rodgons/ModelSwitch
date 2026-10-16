using ModelSwitch.Evaluation;
using ModelSwitch.Identity;
using ModelSwitch.Inference;
using ModelSwitch.Registry;
using ModelSwitch.Routing;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Hosting;
using ModelSwitch.SharedKernel.Jobs;
using ModelSwitch.Usage;
using Npgsql;

// Invalid deployment configuration exits with code 78 and names the variable (FR-10).
return await StartupExit.RunAsync(
    async () =>
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.AddEnvironmentVariables(RequiredSetting.EnvironmentPrefix);

        builder.Services.AddSharedKernel();
        builder.Services.AddUsageModule();
        builder.Services.AddEvaluationModule();
        builder.Services.AddRegistryModule();
        builder.Services.AddInferenceModule();
        builder.Services.AddIdentityModule();
        builder.Services.AddRoutingModule();

        // Jobs infrastructure: claim loops, recurring-job scheduler and outbox relay. Modules contribute handlers, schedules and
        // outbox consumers from their own registration. The usage rollup consumer arrives with Phase 4.
        builder.Services.AddJobEnqueuer(provider => NpgsqlDataSource.Create(provider.GetRequiredService<IConfiguration>()["ConnectionStrings:Postgres"]!));
        builder.Services.AddJobsWorker();

        await builder.Build().RunAsync();
        return 0;
    },
    Console.Error);
