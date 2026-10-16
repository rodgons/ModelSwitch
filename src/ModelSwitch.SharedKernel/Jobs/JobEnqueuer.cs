using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Options;

namespace ModelSwitch.SharedKernel.Jobs;

internal static class JobSql
{
    public static DbParameter Add(DbCommand command, string name, object? value, DbType? type = null)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        if (type is { } dbType)
        {
            parameter.DbType = dbType;
        }

        command.Parameters.Add(parameter);
        return parameter;
    }

    /// <summary>Keeps <c>last_error</c> short and free of anything but the exception type and a bounded message (ADR-0010).</summary>
    public static string DescribeError(Exception exception)
    {
        var message = $"{exception.GetType().Name}: {exception.Message}";
        return message.Length <= 500 ? message : message[..500];
    }
}

internal sealed class JobEnqueuer(DbDataSource dataSource, TimeProvider time, IGuidGenerator ids, IOptions<JobsOptions> options) : IJobEnqueuer
{
    private const string _insert = """
        INSERT INTO jobs.job (id, kind, payload, status, attempts, max_attempts, run_after, dedupe_key, created_at, updated_at)
        SELECT @id, @kind, @payload::jsonb, 'pending', 0, @max, @run_after, @dedupe, @now, @now
        WHERE NOT (@unique AND EXISTS (SELECT 1 FROM jobs.job WHERE kind = @kind AND status IN ('pending', 'running')))
        ON CONFLICT (dedupe_key) WHERE dedupe_key IS NOT NULL DO NOTHING
        """;

    public async Task<Guid?> EnqueueAsync(DbConnection connection, DbTransaction? transaction, JobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Kind);
        ArgumentNullException.ThrowIfNull(request.PayloadJson);

        var now = time.GetUtcNow();
        var id = ids.NewGuid();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _insert;
        JobSql.Add(command, "id", id);
        JobSql.Add(command, "kind", request.Kind, DbType.String);
        JobSql.Add(command, "payload", request.PayloadJson, DbType.String);
        JobSql.Add(command, "max", request.MaxAttempts ?? options.Value.MaxAttempts);
        JobSql.Add(command, "run_after", request.RunAfter ?? now);
        JobSql.Add(command, "dedupe", request.DedupeKey, DbType.String);
        JobSql.Add(command, "now", now);
        JobSql.Add(command, "unique", request.UniqueWhileActive);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1 ? id : null;
    }

    public async Task<Guid?> EnqueueAsync(JobRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await EnqueueAsync(connection, null, request, cancellationToken);
    }
}
