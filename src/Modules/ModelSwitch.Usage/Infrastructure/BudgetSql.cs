using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ModelSwitch.SharedKernel;
using Npgsql;

namespace ModelSwitch.Usage.Infrastructure;

/// <summary>
/// The statements of the Reservation model (usage-and-budgets.md section 3), run on the caller's own connection and transaction.
/// They are plain SQL because correctness under concurrency rests on single atomic <c>UPDATE ... WHERE</c> statements.
/// </summary>
/// <remarks>
/// <para>
/// Only a Tenant with a Budget has <c>budget_period</c> rows, so a Tenant without one never takes a row lock on the request path.
/// The invariant, for every row: <c>reserved_amount</c> is the sum of the Tenant's <c>held</c> Reservations of that month and
/// <c>spent_amount</c> the sum of the Usage Records of the requests that started in that month.
/// </para>
/// <para>
/// A row is created (<see cref="SeedPeriodSql"/>) from the Usage Records and held Reservations that already exist, so creating it
/// must not race with a request writing either. Every statement that moves a row first takes the Tenant's Budget lock in shared
/// mode (<see cref="SharedLockSql"/>, in an earlier statement of the same transaction, so its snapshot is taken after the lock);
/// creating or deleting a row takes it in exclusive mode (<see cref="ExclusiveLockSql"/>). Shared holders never wait for each other.
/// </para>
/// </remarks>
internal static class BudgetSql
{
    public const string UsdCurrency = Money.Usd;

    /// <summary>The first key of the two-key advisory lock: "MSBP" (ModelSwitch Budget period). The second is <see cref="LockKey"/>.</summary>
    public const int LockSpace = 0x4D53_4250;

    public const string SharedLockSql = "SELECT pg_advisory_xact_lock_shared(@lock_space, @lock_key)";

    public const string ExclusiveLockSql = "SELECT pg_advisory_xact_lock(@lock_space, @lock_key)";

    /// <summary>
    /// Creates the period row from the standing Budget, if there is one, with the month's held Reservations and Usage Records.
    /// Run under the exclusive lock. Does nothing if the row exists.
    /// </summary>
    public const string SeedPeriodSql =
        """
        INSERT INTO usage.budget_period (tenant_id, period_start, limit_amount, reserved_amount, spent_amount, currency)
        SELECT @tenant, @period, b.limit_amount,
               (SELECT coalesce(sum(r.amount), 0) FROM usage.reservation r
                 WHERE r.tenant_id = @tenant AND r.period_start = @period AND r.status = 'held'),
               (SELECT coalesce(sum(u.total_cost), 0) FROM usage.usage_record u
                 WHERE u.tenant_id = @tenant AND u.recorded_at >= @period_from
                   AND EXISTS (SELECT 1 FROM usage.inference_request q
                                WHERE q.id = u.inference_request_id AND q.started_at >= @period_from AND q.started_at < @period_to)),
               @currency
          FROM usage.budget b
         WHERE b.tenant_id = @tenant
        ON CONFLICT (tenant_id, period_start) DO NOTHING
        """;

    /// <summary>The first day of the UTC calendar month containing <paramref name="instant"/>.</summary>
    public static DateOnly PeriodOf(DateTimeOffset instant) => new(instant.UtcDateTime.Year, instant.UtcDateTime.Month, 1);

    /// <summary>The half-open UTC instant range of a period.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) Bounds(DateOnly period)
    {
        var from = new DateTimeOffset(period.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return (from, from.AddMonths(1));
    }

    /// <summary>The second key of the Tenant's Budget lock. Two Tenants sharing a key only wait for each other while a Budget changes.</summary>
    public static int LockKey(Guid tenantId)
    {
        Span<byte> bytes = stackalloc byte[16];
        tenantId.TryWriteBytes(bytes);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes) ^ BinaryPrimitives.ReadInt32LittleEndian(bytes[4..])
            ^ BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]) ^ BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
    }

    /// <summary>The parameters of the Tenant's Budget lock, for <see cref="SharedLockSql"/> and <see cref="ExclusiveLockSql"/>.</summary>
    public static (string Name, object Value)[] LockParameters(Guid tenantId) => [("lock_space", LockSpace), ("lock_key", LockKey(tenantId))];

    /// <summary>The parameters of <see cref="SeedPeriodSql"/>.</summary>
    public static (string Name, object Value)[] SeedParameters(Guid tenantId, DateOnly period)
    {
        var (from, to) = Bounds(period);
        return [("tenant", tenantId), ("period", period), ("period_from", from), ("period_to", to), ("currency", UsdCurrency)];
    }

    /// <summary>
    /// Creates the period row in its own transaction, under the exclusive lock, when a Reservation finds that its Tenant has a Budget
    /// but no row for the month yet (the first request of a month).
    /// </summary>
    public static async Task SeedPeriodAsync(UsageDbContext db, Guid tenantId, DateOnly period, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await using var batch = Batch(db, (ExclusiveLockSql, LockParameters(tenantId)), (SeedPeriodSql, SeedParameters(tenantId, period)));
        await batch.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public static async Task<int> ExecuteAsync(UsageDbContext db, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(db, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static NpgsqlCommand Command(UsageDbContext db, string sql, params (string Name, object Value)[] parameters)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var command = new NpgsqlCommand(sql, connection, (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    /// <summary>
    /// Several statements in one round trip. Each is its own statement, so under READ COMMITTED each takes a fresh snapshot: a
    /// statement after the Budget lock sees everything committed before the lock was granted.
    /// </summary>
    public static NpgsqlBatch Batch(UsageDbContext db, params (string Sql, (string Name, object Value)[] Parameters)[] statements)
        => Batch((NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction(), statements);

    public static NpgsqlBatch Batch(NpgsqlConnection connection, NpgsqlTransaction? transaction, params (string Sql, (string Name, object Value)[] Parameters)[] statements)
    {
        var batch = new NpgsqlBatch(connection, transaction);
        foreach (var (sql, parameters) in statements)
        {
            var command = new NpgsqlBatchCommand(sql);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            batch.BatchCommands.Add(command);
        }

        return batch;
    }
}
