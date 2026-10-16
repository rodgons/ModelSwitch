using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelSwitch.Configuration;
using ModelSwitch.Evaluation;
using ModelSwitch.Identity;
using ModelSwitch.Registry;
using ModelSwitch.Routing;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Jobs;
using ModelSwitch.Usage;
using Npgsql;

namespace ModelSwitch.IntegrationTests.Infrastructure;

/// <summary>Builds the module services the way the hosts do, against a test database.</summary>
public static class ModuleServices
{
    public static readonly string Pepper = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray());

    /// <summary>Services the Migrator host registers: migrations for every module, plus seeding.</summary>
    public static ServiceProvider ForMigrator(string connectionString, TimeProvider? time = null)
    {
        var services = Base(connectionString, time);
        services.AddIdentityMigrations();
        services.AddRegistryMigrations();
        services.AddRoutingMigrations();
        services.AddUsageMigrations();
        services.AddEvaluationMigrations();
        services.AddConfigurationMigrations();
        services.AddIdentitySeeding();
        return services.BuildServiceProvider();
    }

    /// <summary>Services the api host registers for API Key authentication.</summary>
    public static ServiceProvider ForApi(string connectionString, TimeProvider? time = null)
    {
        var services = Base(connectionString, time);
        services.AddIdentityModule();
        return services.BuildServiceProvider();
    }

    /// <summary>Services the api and worker register for usage: the ledger, budgets and the Reservation sweeper.</summary>
    public static ServiceProvider ForUsage(string connectionString, TimeProvider? time = null)
    {
        var services = Base(connectionString, time);
        services.AddUsageModule();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The jobs infrastructure the worker host registers (enqueuer, processor, scheduler, relay), plus whatever
    /// <paramref name="configure"/> adds (handlers, outbox consumers, a module). Hosted services are registered but not started.
    /// </summary>
    public static ServiceProvider ForJobs(string connectionString, TimeProvider? time = null, Action<IServiceCollection>? configure = null, Action<JobsOptions>? options = null)
    {
        var services = Base(connectionString, time);
        services.AddJobEnqueuer(_ => NpgsqlDataSource.Create(connectionString), options);
        services.AddJobsWorker();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>Services the api host registers for routing: the router, the validator and the policy store.</summary>
    public static ServiceProvider ForRouting(string connectionString)
    {
        var services = Base(connectionString, time: null);
        services.AddIdentityModule();
        services.AddRegistryModule();
        services.AddRoutingModule();
        return services.BuildServiceProvider();
    }

    /// <summary>Services the api host registers for the Registry: credential resolution and the Provider secret store.</summary>
    public static ServiceProvider ForRegistry(string connectionString, string? masterKey, Action<ILoggingBuilder>? logging = null, Dictionary<string, string?>? extra = null)
    {
        var settings = extra is null ? [] : new Dictionary<string, string?>(extra);
        if (masterKey is not null)
        {
            settings["Security:MasterKey"] = masterKey;
        }

        var services = Base(connectionString, null, settings);
        if (logging is not null)
        {
            services.AddLogging(logging);
        }

        services.AddIdentityModule();
        services.AddRegistryModule();
        return services.BuildServiceProvider();
    }

    private static ServiceCollection Base(string connectionString, TimeProvider? time, Dictionary<string, string?>? extra = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString,
                ["ConnectionStrings:PostgresMigrator"] = connectionString,
                ["Security:ApiKeyPepper"] = Pepper,
            })
            .AddInMemoryCollection(extra ?? [])
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSharedKernel();
        if (time is not null)
        {
            services.AddSingleton(time);
        }

        return services;
    }
}
