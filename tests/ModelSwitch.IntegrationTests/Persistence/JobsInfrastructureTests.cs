using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Jobs;
using ModelSwitch.SharedKernel.Outbox;
using ModelSwitch.Usage;
using ModelSwitch.Usage.Contracts;
using Npgsql;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Persistence;

/// <summary>Claim, lease, retry, dead-letter, recurring jobs and the outbox relay against a real database (architecture.md section 2).</summary>
public sealed class JobsInfrastructureTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly List<ServiceProvider> _hosts = [];
    private string _connectionString = null!;

    public async ValueTask InitializeAsync() => _connectionString = await postgres.CreateMigratedDatabaseAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
    }

    private sealed class DelegateHandler(string kind, Func<JobContext, CancellationToken, Task> run) : IJobHandler
    {
        public string Kind => kind;

        public Task HandleAsync(JobContext context, CancellationToken cancellationToken) => run(context, cancellationToken);
    }

    private sealed class DelegateConsumer(string name, string eventType, Func<OutboxEvent, DbConnection, DbTransaction, Task> run) : IOutboxConsumer
    {
        public string Name => name;

        public string EventType => eventType;

        public Task HandleAsync(OutboxEvent outboxEvent, DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
            => run(outboxEvent, connection, transaction);
    }

    private ServiceProvider Host(string worker, Action<IServiceCollection>? configure = null, Action<JobsOptions>? options = null)
    {
        var host = ModuleServices.ForJobs(
            _connectionString,
            _time,
            configure,
            o =>
            {
                o.WorkerId = worker;
                options?.Invoke(o);
            });
        _hosts.Add(host);
        return host;
    }

    private static JobProcessor Processor(ServiceProvider host) => host.GetRequiredService<JobProcessor>();

    private static IJobEnqueuer Enqueuer(ServiceProvider host) => host.GetRequiredService<IJobEnqueuer>();

    [Fact]
    public async Task Two_workers_never_process_the_same_job_twice()
    {
        var seen = new ConcurrentBag<Guid>();
        Action<IServiceCollection> handler = s => s.AddSingleton<IJobHandler>(new DelegateHandler("test.work", async (job, ct) =>
        {
            seen.Add(job.Id);
            await Task.Delay(5, ct);
        }));
        var a = Host("worker-a", handler);
        var b = Host("worker-b", handler);
        for (var i = 0; i < 40; i++)
        {
            await Enqueuer(a).EnqueueAsync(new JobRequest("test.work"), CancellationToken.None);
        }

        async Task Drain(ServiceProvider host)
        {
            while (await Processor(host).ProcessOneAsync(CancellationToken.None))
            {
            }
        }

        await Task.WhenAll(Drain(a), Drain(b), Drain(a), Drain(b));

        seen.Count.ShouldBe(40);
        seen.Distinct().Count().ShouldBe(40);
        (await Scalar("select count(*) from jobs.job where status = 'succeeded' and attempts = 1")).ShouldBe("40");
    }

    [Fact]
    public async Task A_job_is_not_claimed_before_its_run_after()
    {
        var runs = 0;
        var host = Host("worker-a", s => s.AddSingleton<IJobHandler>(new DelegateHandler("test.later", (_, _) =>
        {
            runs++;
            return Task.CompletedTask;
        })));
        await Enqueuer(host).EnqueueAsync(new JobRequest("test.later", RunAfter: _time.GetUtcNow().AddMinutes(1)), CancellationToken.None);

        (await Processor(host).ProcessOneAsync(CancellationToken.None)).ShouldBeFalse();
        _time.Advance(TimeSpan.FromMinutes(1));
        (await Processor(host).ProcessOneAsync(CancellationToken.None)).ShouldBeTrue();
        runs.ShouldBe(1);
    }

    [Fact]
    public async Task A_job_whose_lease_expired_is_reclaimed_by_another_worker()
    {
        var attempts = new List<int>();
        var worker = Host("worker-b", s => s.AddSingleton<IJobHandler>(new DelegateHandler("test.crash", (job, _) =>
        {
            attempts.Add(job.Attempt);
            return Task.CompletedTask;
        })));
        var id = (await Enqueuer(worker).EnqueueAsync(new JobRequest("test.crash"), CancellationToken.None)).ShouldNotBeNull();

        // worker-a claimed it and then died: running, one attempt used, lease in the future.
        await Execute($"update jobs.job set status = 'running', attempts = 1, locked_by = 'worker-a', locked_until = '{_time.GetUtcNow().AddSeconds(60):o}' where id = '{id}'");
        (await Processor(worker).ProcessOneAsync(CancellationToken.None)).ShouldBeFalse("the lease is still valid");

        _time.Advance(TimeSpan.FromSeconds(61));
        (await Processor(worker).ProcessOneAsync(CancellationToken.None)).ShouldBeTrue();

        attempts.ShouldBe([2]);
        (await Rows($"select status, attempts::text, locked_by from jobs.job where id = '{id}'")).Single().ShouldBe(["succeeded", "2", null]);
    }

    [Fact]
    public async Task A_job_whose_lease_expired_on_its_final_attempt_is_dead_lettered_not_run_again()
    {
        var runs = 0;
        var worker = Host("worker-b", s => s.AddSingleton<IJobHandler>(new DelegateHandler("test.crash", (_, _) =>
        {
            runs++;
            return Task.CompletedTask;
        })));
        var id = (await Enqueuer(worker).EnqueueAsync(new JobRequest("test.crash", MaxAttempts: 3), CancellationToken.None)).ShouldNotBeNull();
        await Execute($"update jobs.job set status = 'running', attempts = 3, locked_by = 'worker-a', locked_until = '{_time.GetUtcNow().AddSeconds(5):o}' where id = '{id}'");

        _time.Advance(TimeSpan.FromSeconds(6));
        (await Processor(worker).ProcessOneAsync(CancellationToken.None)).ShouldBeFalse();

        runs.ShouldBe(0);
        (await Scalar($"select status from jobs.job where id = '{id}'")).ShouldBe("dead");
    }

    [Fact]
    public async Task A_running_job_keeps_its_lease_through_heartbeats()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var a = Host("worker-a", s => s.AddSingleton<IJobHandler>(new DelegateHandler("test.long", async (_, _) =>
        {
            started.SetResult();
            await release.Task;
        })));
        var b = Host("worker-b", s => s.AddSingleton<IJobHandler>(new DelegateHandler("test.long", (_, _) => Task.CompletedTask)));
        var id = (await Enqueuer(a).EnqueueAsync(new JobRequest("test.long"), CancellationToken.None)).ShouldNotBeNull();

        var running = Processor(a).ProcessOneAsync(CancellationToken.None);
        await started.Task;

        // 90 fake seconds pass, longer than the 60 s lease. Each 15 s beat has to extend it.
        for (var beat = 0; beat < 6; beat++)
        {
            var before = await Scalar($"select locked_until::text from jobs.job where id = '{id}'");
            await Task.Delay(100, TestContext.Current.CancellationToken);
            _time.Advance(TimeSpan.FromSeconds(15));
            await WaitUntil(async () => await Scalar($"select locked_until::text from jobs.job where id = '{id}'") != before);
        }

        (await Processor(b).ProcessOneAsync(CancellationToken.None)).ShouldBeFalse("the heartbeat kept the lease valid");
        release.SetResult();
        (await running).ShouldBeTrue();
        (await Scalar($"select status from jobs.job where id = '{id}'")).ShouldBe("succeeded");
    }

    [Fact]
    public async Task A_failing_job_is_retried_with_backoff_and_dead_lettered_after_max_attempts()
    {
        var calls = 0;
        var host = Host("worker-a", s => s.AddSingleton<IJobHandler>(new DelegateHandler("test.fail", (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("boom");
        })));
        var id = (await Enqueuer(host).EnqueueAsync(new JobRequest("test.fail", MaxAttempts: 3), CancellationToken.None)).ShouldNotBeNull();

        (await Processor(host).ProcessOneAsync(CancellationToken.None)).ShouldBeTrue();
        (await Rows($"select status, attempts::text from jobs.job where id = '{id}'")).Single().ShouldBe(["pending", "1"]);
        (await Processor(host).ProcessOneAsync(CancellationToken.None)).ShouldBeFalse("it is backing off");

        _time.Advance(TimeSpan.FromSeconds(5));
        (await Processor(host).ProcessOneAsync(CancellationToken.None)).ShouldBeTrue();
        (await Processor(host).ProcessOneAsync(CancellationToken.None)).ShouldBeFalse("the second backoff is 10 s");

        _time.Advance(TimeSpan.FromSeconds(10));
        (await Processor(host).ProcessOneAsync(CancellationToken.None)).ShouldBeTrue();

        calls.ShouldBe(3);
        (await Rows($"select status, attempts::text, last_error from jobs.job where id = '{id}'")).Single().ShouldBe(["dead", "3", "InvalidOperationException: boom"]);
        _time.Advance(TimeSpan.FromDays(1));
        (await Processor(host).ProcessOneAsync(CancellationToken.None)).ShouldBeFalse("a dead job is never claimed");
        calls.ShouldBe(3);
    }

    [Fact]
    public async Task A_permanent_failure_dead_letters_at_once()
    {
        var host = Host("worker-a", s => s.AddSingleton<IJobHandler>(new DelegateHandler("test.fatal", (_, _) => throw new PermanentJobFailureException("never works"))));
        var id = (await Enqueuer(host).EnqueueAsync(new JobRequest("test.fatal"), CancellationToken.None)).ShouldNotBeNull();

        await Processor(host).ProcessOneAsync(CancellationToken.None);

        (await Rows($"select status, attempts::text from jobs.job where id = '{id}'")).Single().ShouldBe(["dead", "1"]);
    }

    [Fact]
    public async Task A_job_enqueued_in_a_rolled_back_transaction_does_not_exist()
    {
        var host = Host("worker-a");
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await Enqueuer(host).EnqueueAsync(connection, transaction, new JobRequest("test.tx"), CancellationToken.None);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await Enqueuer(host).EnqueueAsync(connection, transaction, new JobRequest("test.tx"), CancellationToken.None);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        (await Scalar("select count(*) from jobs.job where kind = 'test.tx'")).ShouldBe("1");
    }

    [Fact]
    public async Task A_dedupe_key_and_unique_while_active_each_ignore_a_second_enqueue()
    {
        var host = Host("worker-a");
        var enqueuer = Enqueuer(host);

        (await enqueuer.EnqueueAsync(new JobRequest("test.dedupe", DedupeKey: "k"), CancellationToken.None)).ShouldNotBeNull();
        (await enqueuer.EnqueueAsync(new JobRequest("test.dedupe", DedupeKey: "k"), CancellationToken.None)).ShouldBeNull();
        (await enqueuer.EnqueueAsync(new JobRequest("test.unique", UniqueWhileActive: true), CancellationToken.None)).ShouldNotBeNull();
        (await enqueuer.EnqueueAsync(new JobRequest("test.unique", UniqueWhileActive: true), CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Recurring_jobs_run_once_per_interval_however_many_replicas_schedule_them()
    {
        var runs = 0;
        Action<IServiceCollection> recurring = s =>
        {
            s.AddSingleton<IJobHandler>(new DelegateHandler("test.recurring", (_, _) =>
            {
                Interlocked.Increment(ref runs);
                return Task.CompletedTask;
            }));
            s.AddRecurringJob("test.recurring", TimeSpan.FromSeconds(30));
        };
        var a = Host("worker-a", recurring);
        var b = Host("worker-b", recurring);

        for (var interval = 0; interval < 3; interval++)
        {
            var inserted = await a.GetRequiredService<RecurringJobScheduler>().ScheduleDueAsync(CancellationToken.None)
                + await b.GetRequiredService<RecurringJobScheduler>().ScheduleDueAsync(CancellationToken.None);
            inserted.ShouldBe(1, "exactly one replica wins the slot");
            while (await Processor(a).ProcessOneAsync(CancellationToken.None) || await Processor(b).ProcessOneAsync(CancellationToken.None))
            {
            }

            _time.Advance(TimeSpan.FromSeconds(30));
        }

        runs.ShouldBe(3);
    }

    [Fact]
    public async Task The_relay_delivers_each_event_to_its_consumer_exactly_once_across_two_relays()
    {
        var delivered = new ConcurrentBag<Guid>();
        Action<IServiceCollection> consumer = s => s.AddSingleton<IOutboxConsumer>(new DelegateConsumer("test.counter", OutboxWriter.UsageRequestCompleted, async (e, connection, transaction) =>
        {
            delivered.Add(e.Id);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"insert into public.effects values ('{e.Id}')"; // a duplicate delivery would violate the primary key
            await command.ExecuteNonQueryAsync();
        }));
        var a = Host("worker-a", consumer);
        var b = Host("worker-b", consumer);
        await Execute("create table public.effects (event_id uuid primary key)");
        for (var i = 0; i < 25; i++)
        {
            await Append(OutboxWriter.UsageRequestCompleted);
        }

        var counts = await Task.WhenAll(
            a.GetRequiredService<OutboxRelay>().RelayAsync(100, CancellationToken.None),
            b.GetRequiredService<OutboxRelay>().RelayAsync(100, CancellationToken.None),
            a.GetRequiredService<OutboxRelay>().RelayAsync(100, CancellationToken.None));

        counts.Sum().ShouldBe(25);
        delivered.Distinct().Count().ShouldBe(25);
        delivered.Count.ShouldBe(25);
        (await Scalar("select count(*) from jobs.outbox_event where published_at is null")).ShouldBe("0");
        (await Scalar("select count(*) from jobs.processed_event where consumer = 'test.counter'")).ShouldBe("25");
    }

    [Fact]
    public async Task Redelivery_of_an_already_processed_event_is_a_no_op()
    {
        var calls = 0;
        var host = Host("worker-a", s => s.AddSingleton<IOutboxConsumer>(new DelegateConsumer("test.once", "test.event", (_, _, _) =>
        {
            calls++;
            return Task.CompletedTask;
        })));
        var id = await Append("test.event");
        var relay = host.GetRequiredService<OutboxRelay>();
        (await relay.RelayAsync(10, CancellationToken.None)).ShouldBe(1);

        // A crash between the consumer's commit and the publish mark cannot happen (one transaction), but an operator can reset the flag.
        await Execute($"update jobs.outbox_event set published_at = null where id = '{id}'");
        (await relay.RelayAsync(10, CancellationToken.None)).ShouldBe(1);

        calls.ShouldBe(1);
        (await Scalar($"select published_at is not null from jobs.outbox_event where id = '{id}'")).ShouldBe("True");
    }

    [Fact]
    public async Task A_failing_consumer_rolls_the_event_back_and_retries_it_later_without_blocking_other_events()
    {
        var poison = Guid.Empty;
        var failing = true;
        var delivered = new List<Guid>();
        var host = Host("worker-a", s => s.AddSingleton<IOutboxConsumer>(new DelegateConsumer("test.flaky", "test.event", (e, _, _) =>
        {
            if (e.Id == poison && failing)
            {
                throw new InvalidOperationException("consumer down");
            }

            delivered.Add(e.Id);
            return Task.CompletedTask;
        })));
        poison = await Append("test.event");
        var healthy = await Append("test.event");
        var relay = host.GetRequiredService<OutboxRelay>();

        (await relay.RelayAsync(10, CancellationToken.None)).ShouldBe(1);

        delivered.ShouldBe([healthy]);
        (await Rows($"select published_at is null, attempts::text, last_error from jobs.outbox_event where id = '{poison}'")).Single()
            .ShouldBe(["True", "1", "InvalidOperationException: consumer down"]);
        (await Scalar($"select count(*) from jobs.processed_event where event_id = '{poison}'")).ShouldBe("0", "the failed delivery was rolled back");

        (await relay.RelayAsync(10, CancellationToken.None)).ShouldBe(0, "the event is backing off");
        failing = false;
        _time.Advance(TimeSpan.FromSeconds(5));
        (await relay.RelayAsync(10, CancellationToken.None)).ShouldBe(1);
        delivered.ShouldBe([healthy, poison]);
    }

    [Fact]
    public async Task Events_without_a_consumer_stay_unpublished_for_a_consumer_added_later()
    {
        var bare = Host("worker-a");
        var id = await Append(OutboxWriter.UsageRequestCompleted);
        (await bare.GetRequiredService<OutboxRelay>().RelayAsync(10, CancellationToken.None)).ShouldBe(0);

        var seen = new List<Guid>();
        var later = Host("worker-b", s => s.AddSingleton<IOutboxConsumer>(new DelegateConsumer("test.late", OutboxWriter.UsageRequestCompleted, (e, _, _) =>
        {
            seen.Add(e.Id);
            return Task.CompletedTask;
        })));
        (await later.GetRequiredService<OutboxRelay>().RelayAsync(10, CancellationToken.None)).ShouldBe(1);

        seen.ShouldBe([id]);
    }

    [Fact]
    public async Task An_abandoned_Reservation_is_swept_by_the_scheduled_worker_job_within_one_interval()
    {
        var host = Host("worker-a", s => s.AddUsageModule());
        var ledger = host.GetRequiredService<IUsageLedger>();
        var tenant = Guid.CreateVersion7();
        var requestId = Guid.CreateVersion7();
        await ledger.OpenAsync(
            new OpenInferenceRequest(requestId, tenant, RequestOrigin.Api, Guid.CreateVersion7(), null, null, "balanced", null, false, 10, string.Empty, new ReservationRequest(Money.FromUsd(0.5m), _time.GetUtcNow().AddSeconds(20))),
            CancellationToken.None);
        var scheduler = host.GetRequiredService<RecurringJobScheduler>();

        // Before the Reservation expires the scheduled sweep finds nothing.
        await scheduler.ScheduleDueAsync(CancellationToken.None);
        await Processor(host).ProcessOneAsync(CancellationToken.None);
        (await Scalar($"select status from usage.inference_request where id = '{requestId}'")).ShouldNotBe("abandoned");

        _time.Advance(TimeSpan.FromSeconds(30)); // the expiry has passed and the next 30 s slot has begun
        (await scheduler.ScheduleDueAsync(CancellationToken.None)).ShouldBe(1);
        (await Processor(host).ProcessOneAsync(CancellationToken.None)).ShouldBeTrue();

        (await Scalar($"select status from usage.inference_request where id = '{requestId}'")).ShouldBe("abandoned");
        (await Scalar($"select count(*) from jobs.outbox_event where payload->>'requestId' = '{requestId}' and payload->>'status' = 'abandoned'")).ShouldBe("1");
    }

    private async Task<Guid> Append(string type)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await OutboxWriter.AppendAsync(connection, null, id, type, "{}", _time.GetUtcNow(), CancellationToken.None);
        return id;
    }

    private async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string?> Scalar(string sql) => (await Rows(sql)).SingleOrDefault()?[0];

    private async Task<List<string?[]>> Rows(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string?[]>();
        while (await reader.ReadAsync())
        {
            rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).ToArray());
        }

        return rows;
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in 10 s.");
            }

            await Task.Delay(25);
        }
    }
}
