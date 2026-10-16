using System.Data.Common;

namespace ModelSwitch.SharedKernel.Jobs;

internal sealed class JobStatusReader(DbDataSource dataSource) : IJobStatusReader
{
    public async Task<IReadOnlyDictionary<string, string>> StatusesByDedupeKeyAsync(IReadOnlyCollection<string> dedupeKeys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dedupeKeys);
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        if (dedupeKeys.Count == 0)
        {
            return statuses;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT dedupe_key, status FROM jobs.job WHERE dedupe_key = ANY(@keys)";
        JobSql.Add(command, "keys", dedupeKeys.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            statuses[reader.GetString(0)] = reader.GetString(1);
        }

        return statuses;
    }
}
