using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(ModelSwitch.IntegrationTests.Infrastructure.PostgresFixture))]

namespace ModelSwitch.IntegrationTests.Infrastructure;

/// <summary>
/// One PostgreSQL container per test assembly (testing.md section 1). Each test asks for its own empty database,
/// so tests never see each other's data. Later integration tasks reuse this for the full API test host.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();
    private int _databases;

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Creates a new empty database and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"test_{Interlocked.Increment(ref _databases)}_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name, Pooling = false }.ConnectionString;
    }
}
