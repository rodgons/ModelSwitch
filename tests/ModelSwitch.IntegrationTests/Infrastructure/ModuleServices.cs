using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.Identity;
using ModelSwitch.Registry;
using ModelSwitch.SharedKernel;

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

    private static ServiceCollection Base(string connectionString, TimeProvider? time)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString,
                ["ConnectionStrings:PostgresMigrator"] = connectionString,
                ["Security:ApiKeyPepper"] = Pepper,
            })
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
