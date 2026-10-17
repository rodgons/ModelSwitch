using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.SharedKernel.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(ModelSwitch.IntegrationTests.Infrastructure.PostgresFixture))]

namespace ModelSwitch.IntegrationTests.Infrastructure;

/// <summary>
/// One PostgreSQL container per test assembly (testing.md section 1). Each test class asks for its own database,
/// so tests never see each other's data.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string _image = "postgres:17";

    private readonly PostgreSqlContainer _container;
    private readonly SemaphoreSlim _templateLock = new(1, 1);
    private int _databases;
    private string? _templateName;

    public PostgresFixture()
        : this(new PostgreSqlBuilder(_image).Build())
    {
    }

    private PostgresFixture(PostgreSqlContainer container) => _container = container;

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    /// <summary>
    /// A started PostgreSQL that the caller owns and may stop and start again, for a scenario that takes the database away (the
    /// shared container must keep running for every other test class). Its host port is fixed, so connection strings handed out
    /// before a restart still reach it afterwards. The caller disposes it.
    /// </summary>
    public static async Task<PostgresFixture> StartDedicatedAsync()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var fixture = new PostgresFixture(new PostgreSqlBuilder(_image).WithPortBinding(port, PostgreSqlBuilder.PostgreSqlPort).Build());
        await fixture.InitializeAsync();
        return fixture;
    }

    /// <summary>Stops the container, as when the database host goes down: connections are refused until <see cref="StartAsync"/>.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => _container.StopAsync(cancellationToken);

    /// <summary>Starts a stopped container again, with its data and host port.</summary>
    public Task StartAsync(CancellationToken cancellationToken) => _container.StartAsync(cancellationToken);

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Creates a new empty database and returns its connection string.</summary>
    public Task<string> CreateDatabaseAsync() => CreateAsync(template: null);

    /// <summary>
    /// Creates a database with every module's migrations applied, by cloning a template that is migrated once per
    /// assembly (testing.md section 1). Cloning takes milliseconds; migrating takes seconds.
    /// </summary>
    public async Task<string> CreateMigratedDatabaseAsync()
    {
        if (_templateName is null)
        {
            await _templateLock.WaitAsync();
            try
            {
                if (_templateName is null)
                {
                    var connectionString = await CreateAsync(template: null);
                    await using (var services = ModuleServices.ForMigrator(connectionString))
                    {
                        foreach (var migrator in services.GetServices<IModuleMigrator>())
                        {
                            await migrator.MigrateAsync(CancellationToken.None);
                        }
                    }

                    _templateName = new NpgsqlConnectionStringBuilder(connectionString).Database!;
                }
            }
            finally
            {
                _templateLock.Release();
            }
        }

        return await CreateAsync(_templateName);
    }

    private async Task<string> CreateAsync(string? template)
    {
        var name = $"test_{Interlocked.Increment(ref _databases)}_{Guid.CreateVersion7():N}";
        await using (var connection = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = template is null ? $"CREATE DATABASE \"{name}\"" : $"CREATE DATABASE \"{name}\" TEMPLATE \"{template}\"";
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name, Pooling = false }.ConnectionString;
    }
}
