using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelSwitch.Identity;
using ModelSwitch.Migrator;
using ModelSwitch.Registry;
using ModelSwitch.Routing;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Hosting;
using ModelSwitch.Usage;

// Commands: `migrate` (default) and `seed`. Everything after the command is configuration, e.g.
//   dotnet run --project src/ModelSwitch.Migrator -- seed --tenant acme --key-name ci
var command = args.FirstOrDefault(a => !a.StartsWith('-')) ?? MigratorCommand.Migrate;
var configurationArgs = args.Where(a => !string.Equals(a, command, StringComparison.Ordinal)).ToArray();

if (command is not (MigratorCommand.Migrate or MigratorCommand.Seed))
{
    await Console.Error.WriteLineAsync($"Unknown command '{command}'. Use 'migrate' (default) or 'seed'.");
    return 64;
}

return await StartupExit.RunAsync(
    async () =>
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = configurationArgs });
        builder.Configuration.AddEnvironmentVariables(RequiredSetting.EnvironmentPrefix);
        builder.Configuration.AddCommandLine(
            configurationArgs,
            new Dictionary<string, string>
            {
                ["--tenant"] = "Seed:TenantName",
                ["--key-name"] = "Seed:KeyName",
            });

        // stdout carries only what the operator must capture (the seeded key), so logs go to stderr.
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSharedKernel();

        // The fixed migration order (docs/deployment.md section 4). Each module appends itself here.
        builder.Services.AddIdentityMigrations();
        builder.Services.AddRegistryMigrations();
        builder.Services.AddRoutingMigrations();
        builder.Services.AddUsageMigrations();

        if (command == MigratorCommand.Seed)
        {
            builder.Services.AddIdentitySeeding();
            builder.Services.RequireSetting("Seed:TenantName");
        }

        builder.Services.AddSingleton<DatabaseRoles>();
        builder.Services.AddSingleton<MigratorRunner>();

        using var host = builder.Build();
        await host.StartAsync(); // ValidateOnStart: a missing setting throws here, before any database work.
        try
        {
            var runner = host.Services.GetRequiredService<MigratorRunner>();
            return command == MigratorCommand.Seed
                ? await runner.SeedAsync(Console.Out, CancellationToken.None)
                : await runner.MigrateAsync(CancellationToken.None);
        }
        finally
        {
            await host.StopAsync();
        }
    },
    Console.Error);
