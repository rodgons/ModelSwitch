namespace ModelSwitch.SharedKernel.Jobs;

/// <summary>Tuning for the worker's job loop, scheduler and outbox relay. The defaults suit production.</summary>
public sealed class JobsOptions
{
    /// <summary>Identifies this worker in <c>job.locked_by</c>. Null generates a unique id per process.</summary>
    public string? WorkerId { get; set; }

    /// <summary>Concurrent job loops per worker process.</summary>
    public int Concurrency { get; set; } = 4;

    /// <summary>Idle wait between polls when nothing is claimable.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a claim is valid without a heartbeat. After it passes another worker may reclaim the job.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often a running job's lease is extended. Must be well under <see cref="LeaseDuration"/>.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    public int MaxAttempts { get; set; } = 5;

    /// <summary>The wait after the first failed attempt. It doubles per attempt up to <see cref="BackoffMax"/>.</summary>
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan BackoffMax { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Outbox events relayed per pass.</summary>
    public int OutboxBatchSize { get; set; } = 100;
}
