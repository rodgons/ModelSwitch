using ModelSwitch.Configuration.Application;
using ModelSwitch.SharedKernel.Configuration;
using Npgsql;

namespace ModelSwitch.Configuration.Infrastructure;

internal sealed class NpgsqlVersionReader(string connectionString) : IConfigurationVersionReader
{
    public async Task<long> ReadAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await ConfigVersion.ReadAsync(connection, cancellationToken);
    }
}
