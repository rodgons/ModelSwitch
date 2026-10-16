using System.Data.Common;

namespace ModelSwitch.SharedKernel.Jobs;

/// <summary>A unit of background work to enqueue (data-model.md "Background processing").</summary>
/// <param name="Kind">Selects the <see cref="IJobHandler"/>.</param>
/// <param name="PayloadJson">Metadata only, never prompt or completion content (ADR-0010).</param>
/// <param name="RunAfter">Earliest start. <see langword="null"/> means now.</param>
/// <param name="MaxAttempts">Attempts before the job is dead-lettered. <see langword="null"/> uses <see cref="JobsOptions.MaxAttempts"/>.</param>
/// <param name="DedupeKey">A second enqueue with the same key is ignored (it returns <see langword="null"/>).</param>
/// <param name="UniqueWhileActive">Ignore the enqueue while another job of the same kind is pending or running.</param>
public sealed record JobRequest(
    string Kind,
    string PayloadJson = "{}",
    DateTimeOffset? RunAfter = null,
    int? MaxAttempts = null,
    string? DedupeKey = null,
    bool UniqueWhileActive = false);

/// <summary>Enqueues jobs. The overload taking a connection joins the caller's transaction, so the job exists only if the caller commits.</summary>
public interface IJobEnqueuer
{
    /// <summary>Returns the new job id, or <see langword="null"/> if a dedupe rule ignored the request.</summary>
    Task<Guid?> EnqueueAsync(DbConnection connection, DbTransaction? transaction, JobRequest request, CancellationToken cancellationToken);

    /// <summary>Enqueues on its own connection.</summary>
    Task<Guid?> EnqueueAsync(JobRequest request, CancellationToken cancellationToken);
}

/// <summary>What a handler is told about the job it runs.</summary>
/// <param name="Id">The job id.</param>
/// <param name="Kind">The job kind.</param>
/// <param name="PayloadJson">The enqueued payload.</param>
/// <param name="Attempt">1 on the first run.</param>
/// <param name="MaxAttempts">Attempts allowed before dead-lettering.</param>
public sealed record JobContext(Guid Id, string Kind, string PayloadJson, int Attempt, int MaxAttempts);

/// <summary>
/// Runs jobs of one kind. Throw to fail the attempt (it is retried with backoff until <see cref="JobContext.MaxAttempts"/>, then
/// dead-lettered). The token is cancelled when the worker stops or the job's lease was lost. Handlers must be idempotent: a
/// lease that expires hands the job to another worker.
/// </summary>
public interface IJobHandler
{
    string Kind { get; }

    Task HandleAsync(JobContext context, CancellationToken cancellationToken);
}

/// <summary>Thrown by a handler to dead-letter the job without further attempts.</summary>
public sealed class PermanentJobFailureException : Exception
{
    public PermanentJobFailureException()
    {
    }

    public PermanentJobFailureException(string message)
        : base(message)
    {
    }

    public PermanentJobFailureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A job of <see cref="Kind"/> that must exist once per <see cref="Interval"/> across all worker replicas.</summary>
public sealed record RecurringJobSchedule(string Kind, TimeSpan Interval);

/// <summary>An event read from <c>jobs.outbox_event</c>.</summary>
public sealed record OutboxEvent(Guid Id, string Type, string PayloadJson, DateTimeOffset OccurredAt);

/// <summary>
/// Consumes one outbox event type. The relay calls <see cref="HandleAsync"/> inside the transaction that records the event in
/// <c>processed_event</c> for <see cref="Name"/>, so a handler that writes through the given connection gets exactly-once
/// effect. Redelivery is a no-op (NFR-06).
/// </summary>
public interface IOutboxConsumer
{
    /// <summary>The idempotency scope: one row per (Name, event id) in <c>processed_event</c>.</summary>
    string Name { get; }

    string EventType { get; }

    Task HandleAsync(OutboxEvent outboxEvent, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>The <c>jobs.job.status</c> values (data-model.md "Background processing").</summary>
public static class JobStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Dead = "dead";
}

/// <summary>
/// Reads job state, for a module that must notice a job it enqueued ending without its handler finishing the work (a worker killed
/// during the final attempt is dead-lettered by the claim loop, which never calls the handler again).
/// </summary>
public interface IJobStatusReader
{
    /// <summary>
    /// The <see cref="JobStatuses"/> value of each job whose dedupe key is in <paramref name="dedupeKeys"/>. A key with no job (never
    /// enqueued, or removed by retention) is absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> StatusesByDedupeKeyAsync(IReadOnlyCollection<string> dedupeKeys, CancellationToken cancellationToken);
}
