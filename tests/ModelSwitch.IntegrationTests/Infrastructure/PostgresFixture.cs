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
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();
    private readonly SemaphoreSlim _templateLock = new(1, 1);
    private int _databases;
    private string? _templateName;

    public async ValueTask InitializeAsync() => await _container.StartAsync();

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
