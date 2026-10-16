using ModelSwitch.SharedKernel.Hosting;
using ModelSwitch.Telemetry;
using ModelSwitch.Worker;

// Invalid deployment configuration exits with code 78 and names the variable (FR-10).
return await StartupExit.RunAsync(
    async () =>
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.AddEnvironmentVariables(RequiredSetting.EnvironmentPrefix);

        builder.AddModelSwitchTelemetry("modelswitch-worker");
        builder.Services.AddWorkerServices();

        await builder.Build().RunAsync();
        return 0;
    },
    Console.Error);
