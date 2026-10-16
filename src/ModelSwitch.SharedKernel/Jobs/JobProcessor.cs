using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ModelSwitch.SharedKernel.Jobs;

/// <summary>
/// Claims one job, runs its handler under a heartbeat-extended lease and records the outcome (architecture.md section 2). Claiming is
/// <c>FOR UPDATE SKIP LOCKED</c>, so any number of workers can run it concurrently without taking the same job twice. A job whose lease
/// expired (the worker died) is claimed again, or dead-lettered if that attempt was its last.
/// </summary>
internal sealed partial class JobProcessor
{
    private readonly DbDataSource _dataSource;
    private readonly IReadOnlyDictionary<string, IJobHandler> _handlers;
    private readonly string[] _kinds;
    private readonly TimeProvider _time;
    private readonly JobsOptions _options;
    private readonly ILogger<JobProcessor> _logger;

    public JobProcessor(
        DbDataSource dataSource,
        IEnumerable<IJobHandler> handlers,
        TimeProvider time,
        IGuidGenerator ids,
        IOptions<JobsOptions> options,
        ILogger<JobProcessor> logger)
    {
        _dataSource = dataSource;
        _handlers = handlers.ToDictionary(h => h.Kind, StringComparer.Ordinal);
        _kinds = [.. _handlers.Keys];
        _time = time;
        _options = options.Value;
        _logger = logger;
        WorkerId = _options.WorkerId ?? $"{Environment.MachineName}:{Environment.ProcessId}:{ids.NewGuid():N}";
    }

    public string WorkerId { get; }

    private readonly record struct Claimed(Guid Id, string Kind, string Payload, int Attempts, int MaxAttempts);

    /// <summary>Runs at most one job. Returns false when nothing was claimable.</summary>
    public async Task<bool> ProcessOneAsync(CancellationToken cancellationToken)
    {
        if (_kinds.Length == 0)
        {
            return false;
        }

        var claimed = await ClaimAsync(cancellationToken);
        if (claimed is not { } job)
        {
            return false;
        }

        LogStarted(_logger, job.Kind, job.Id, job.Attempts, job.MaxAttempts);
        using var lost = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync(job, lost, heartbeatStop.Token);
        try
        {
            await _handlers[job.Kind].HandleAsync(new JobContext(job.Id, job.Kind, job.Payload, job.Attempts, job.MaxAttempts), lost.Token);
            await StopAsync(heartbeat, heartbeatStop);
            await FinishAsync(job, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The worker is stopping: hand the job back without spending an attempt.
            await StopAsync(heartbeat, heartbeatStop);
            await ReleaseAsync(job);
        }
        catch (OperationCanceledException) when (lost.IsCancellationRequested)
        {
            // The lease was lost, so the job now belongs to another worker. Our guarded updates would be no-ops.
            await StopAsync(heartbeat, heartbeatStop);
            LogLeaseLost(_logger, job.Kind, job.Id);
        }
        catch (Exception exception)
        {
            await StopAsync(heartbeat, heartbeatStop);
            await FailAsync(job, exception, cancellationToken);
        }

        return true;
    }

    private static async Task StopAsync(Task heartbeat, CancellationTokenSource stop)
    {
        await stop.CancelAsync();
        await heartbeat;
    }

    private async Task<Claimed?> ClaimAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // A job whose lease expired on its final attempt has used its budget: dead-letter it rather than run it again.
        await using (var reap = connection.CreateCommand())
        {
            reap.CommandText = """
                UPDATE jobs.job SET status = 'dead', locked_by = NULL, locked_until = NULL, updated_at = @now,
                    last_error = 'The lease expired during the final attempt.'
                WHERE status = 'running' AND locked_until < @now AND attempts >= max_attempts AND kind = ANY(@kinds)
                """;
            JobSql.Add(reap, "now", now);
            JobSql.Add(reap, "kinds", _kinds);
            var dead = await reap.ExecuteNonQueryAsync(cancellationToken);
            if (dead > 0)
            {
                LogDeadLettered(_logger, dead);
            }
        }

        await using var claim = connection.CreateCommand();
        claim.CommandText = """
            UPDATE jobs.job SET status = 'running', attempts = attempts + 1, locked_by = @worker, locked_until = @until, updated_at = @now
            WHERE id = (
                SELECT id FROM jobs.job
                WHERE kind = ANY(@kinds)
                  AND ((status = 'pending' AND run_after <= @now) OR (status = 'running' AND locked_until < @now AND attempts < max_attempts))
                ORDER BY run_after, id
                FOR UPDATE SKIP LOCKED
                LIMIT 1)
            RETURNING id, kind, payload::text, attempts, max_attempts
            """;
        JobSql.Add(claim, "worker", WorkerId);
        JobSql.Add(claim, "until", JobRetryPolicy.LeaseUntil(now, _options));
        JobSql.Add(claim, "now", now);
        JobSql.Add(claim, "kinds", _kinds);
        await using var reader = await claim.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new Claimed(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4))
            : null;
    }

    private async Task HeartbeatAsync(Claimed job, CancellationTokenSource lost, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(_options.HeartbeatInterval, _time, stop);
                try
                {
                    var now = _time.GetUtcNow();
                    var extended = await GuardedAsync(
                        job,
                        "SET locked_until = @until, updated_at = @now",
                        c =>
                        {
                            JobSql.Add(c, "until", JobRetryPolicy.LeaseUntil(now, _options));
                            JobSql.Add(c, "now", now);
                        },
                        stop);
                    if (!extended)
                    {
                        await lost.CancelAsync();
                        return;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A database blip: try again at the next beat. The lease still covers the time until then.
                    LogHeartbeatFailed(_logger, exception, job.Kind, job.Id);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped because the handler finished.
        }
    }

    private async Task FinishAsync(Claimed job, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        await GuardedAsync(
            job,
            "SET status = 'succeeded', locked_by = NULL, locked_until = NULL, updated_at = @now",
            c => JobSql.Add(c, "now", now),
            cancellationToken);
        LogSucceeded(_logger, job.Kind, job.Id);
    }

    private async Task FailAsync(Claimed job, Exception exception, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var decision = JobRetryPolicy.AfterFailure(job.Attempts, job.MaxAttempts, exception is PermanentJobFailureException, now, _options);
        var applied = await GuardedAsync(
            job,
            "SET status = @status, run_after = @run_after, last_error = @error, locked_by = NULL, locked_until = NULL, updated_at = @now",
            c =>
            {
                JobSql.Add(c, "status", decision.Dead ? "dead" : "pending", DbType.String);
                JobSql.Add(c, "run_after", decision.RunAfter);
                JobSql.Add(c, "error", JobSql.DescribeError(exception), DbType.String);
                JobSql.Add(c, "now", now);
            },
            cancellationToken);
        if (!applied)
        {
            return;
        }

        if (decision.Dead)
        {
            LogJobDead(_logger, exception, job.Kind, job.Id, job.Attempts);
        }
        else
        {
            LogRetryScheduled(_logger, exception, job.Kind, job.Id, job.Attempts, decision.RunAfter);
        }
    }

    private async Task ReleaseAsync(Claimed job)
    {
        var now = _time.GetUtcNow();
        await GuardedAsync(
            job,
            "SET status = 'pending', attempts = attempts - 1, locked_by = NULL, locked_until = NULL, updated_at = @now",
            c => JobSql.Add(c, "now", now),
            CancellationToken.None);
    }

    // Every outcome is guarded by the claim (worker and attempt number), so a worker whose lease was taken over cannot overwrite the new owner's state.
    private async Task<bool> GuardedAsync(Claimed job, string set, Action<DbCommand> parameters, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE jobs.job {set} WHERE id = @id AND status = 'running' AND locked_by = @worker AND attempts = @attempts";
        JobSql.Add(command, "id", job.Id);
        JobSql.Add(command, "worker", WorkerId);
        JobSql.Add(command, "attempts", job.Attempts);
        parameters(command);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Job {Kind} {JobId} started (attempt {Attempt} of {MaxAttempts}).")]
    private static partial void LogStarted(ILogger logger, string kind, Guid jobId, int attempt, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Job {Kind} {JobId} succeeded.")]
    private static partial void LogSucceeded(ILogger logger, string kind, Guid jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {Kind} {JobId} failed on attempt {Attempt}; retrying at {RunAfter:o}.")]
    private static partial void LogRetryScheduled(ILogger logger, Exception exception, string kind, Guid jobId, int attempt, DateTimeOffset runAfter);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {Kind} {JobId} dead-lettered after {Attempt} attempt(s).")]
    private static partial void LogJobDead(ILogger logger, Exception exception, string kind, Guid jobId, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Count} job(s) dead-lettered because their lease expired during the final attempt.")]
    private static partial void LogDeadLettered(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {Kind} {JobId} lost its lease and was abandoned by this worker.")]
    private static partial void LogLeaseLost(ILogger logger, string kind, Guid jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Extending the lease of job {Kind} {JobId} failed.")]
    private static partial void LogHeartbeatFailed(ILogger logger, Exception exception, string kind, Guid jobId);
}
