using System.Data.Common;
using Microsoft.Extensions.Logging;
using ModelSwitch.SharedKernel.Jobs;

namespace ModelSwitch.SharedKernel.Retention;

/// <summary>
/// The daily retention run (data-model.md, "Retention"): every module's <see cref="IRetentionTask"/> in turn. A task that fails is
/// logged and the others still run; the job fails, and is retried, only when every task failed. Idempotent: a second run deletes
/// nothing new.
/// </summary>
internal sealed partial class RetentionJob(
    IEnumerable<IRetentionTask> tasks,
    RetentionDataSource source,
    RetentionOptions options,
    TimeProvider time,
    ILogger<RetentionJob> logger) : IJobHandler
{
    public const string JobKind = "retention.run";

    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    public string Kind => JobKind;

    public async Task HandleAsync(JobContext context, CancellationToken cancellationToken)
    {
        var run = new RetentionContext(source, options, options.CutoffsAt(time.GetUtcNow()));
        if (!source.IsRetentionRole)
        {
            LogAppRole(logger);
        }

        var failed = 0;
        var all = tasks.ToList();
        foreach (var task in all)
        {
            try
            {
                var removed = await task.RunAsync(run, cancellationToken);
                LogTask(logger, task.Name, removed);
            }
            catch (Exception exception) when (exception is DbException or TimeoutException)
            {
                failed++;
                LogTaskFailed(logger, exception, task.Name);
            }
        }

        if (all.Count > 0 && failed == all.Count)
        {
            throw new InvalidOperationException("Every retention task failed; the run will be retried.");
        }
    }

    [LoggerMessage(EventId = 7101, Level = LogLevel.Information, Message = "Retention {Task}: removed {Removed} row(s) or partition(s).")]
    private static partial void LogTask(ILogger logger, string task, long removed);

    [LoggerMessage(EventId = 7102, Level = LogLevel.Error, Message = "Retention {Task} failed; the other tasks still ran.")]
    private static partial void LogTaskFailed(ILogger logger, Exception exception, string task);

    [LoggerMessage(EventId = 7103, Level = LogLevel.Warning, Message = "Retention runs as the app role because ConnectionStrings:PostgresRetention is not set, so audit records are kept. Set MODELSWITCH__ConnectionStrings__PostgresRetention to the modelswitch_retention role.")]
    private static partial void LogAppRole(ILogger logger);
}
