using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Outbox;
using ModelSwitch.Usage.Contracts;
using ModelSwitch.Usage.Domain;

namespace ModelSwitch.Usage.Infrastructure;

/// <summary>
/// The core of the Reservation sweeper (usage-and-budgets.md section 3). Pessimistic by design: an abandoned request may have been
/// billed, so its whole held amount is charged as <c>unavailable</c> usage.
/// </summary>
internal sealed class EfReservationSweeper(IDbContextFactory<UsageDbContext> contexts, TimeProvider time, IGuidGenerator ids) : IReservationSweeper
{
    private readonly record struct Expired(Guid Id, Guid RequestId, DateTimeOffset RequestStartedAt);

    public async Task<int> SweepAbandonedAsync(int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        var now = time.GetUtcNow();

        List<Expired> expired;
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            expired = await db.Reservations.AsNoTracking()
                .Where(r => r.Status == "held" && r.ExpiresAt < now)
                .OrderBy(r => r.ExpiresAt)
                .Take(batchSize)
                .Select(r => new Expired(r.Id, r.InferenceRequestId, r.CreatedAt))
                .ToListAsync(cancellationToken);
        }

        var swept = 0;
        foreach (var reservation in expired)
        {
            if (await SweepOneAsync(reservation, now, cancellationToken))
            {
                swept++;
            }
        }

        return swept;
    }

    private async Task<bool> SweepOneAsync(Expired reservation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var at = new DateTimeOffset(now.UtcTicks - (now.UtcTicks % 10), TimeSpan.Zero);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The request row is locked first, as settlement does, so the two can never deadlock. If settlement got there first the
        // request is no longer running and there is nothing to recover.
        var identity = await db.InferenceRequests
            .Where(r => r.Id == reservation.RequestId && r.StartedAt == reservation.RequestStartedAt && r.Status == "running")
            .Select(r => new { r.TenantId, r.RequestedAlias })
            .FirstOrDefaultAsync(cancellationToken);
        if (identity is null)
        {
            return false;
        }

        var abandoned = await db.InferenceRequests
            .Where(r => r.Id == reservation.RequestId && r.StartedAt == reservation.RequestStartedAt && r.Status == "running")
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(r => r.Status, "abandoned")
                    .SetProperty(r => r.ErrorCode, ErrorCode.InternalError.Value)
                    .SetProperty(r => r.UsageStatus, "unavailable")
                    .SetProperty(r => r.CompletedAt, at)
                    .SetProperty(r => r.DurationMs, (int?)null),
                cancellationToken);
        if (abandoned == 0)
        {
            return false;
        }

        await using var expire = BudgetSql.Command(
            db,
            "UPDATE usage.reservation SET status = 'expired', settled_at = @now WHERE id = @id AND status = 'held' RETURNING amount, period_start",
            ("now", at),
            ("id", reservation.Id));
        decimal amount;
        DateOnly period;
        await using (var reader = await expire.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return false;
            }

            amount = reader.GetDecimal(0);
            period = reader.GetFieldValue<DateOnly>(1);
        }

        await db.InferenceRequests
            .Where(r => r.Id == reservation.RequestId && r.StartedAt == reservation.RequestStartedAt)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.TotalEstimatedCost, amount), cancellationToken);

        db.UsageRecords.Add(new UsageRecordRow
        {
            Id = ids.NewGuid(),
            InferenceRequestId = reservation.RequestId,
            TenantId = identity.TenantId,
            UsageSource = "unavailable",
            EstimateMethod = AttemptCosting.ReservationUpperBound,

            // The cost is the whole held amount; it cannot be split into input and output because nothing is known.
            TotalCost = amount,
            Currency = Money.Usd,
            RecordedAt = at,
        });
        await db.SaveChangesAsync(cancellationToken);

        await BudgetSql.SettleAsync(db, identity.TenantId, period, amount, amount, cancellationToken);

        var payload = JsonSerializer.Serialize(new
        {
            requestId = reservation.RequestId,
            tenantId = identity.TenantId,
            requestedAlias = identity.RequestedAlias,
            status = "abandoned",
            usageStatus = "unavailable",
            attemptCount = 0,
            estimatedCost = amount,
            currency = Money.Usd,
            startedAt = reservation.RequestStartedAt,
        });
        await OutboxWriter.AppendAsync(
            db.Database.GetDbConnection(),
            db.Database.CurrentTransaction!.GetDbTransaction(),
            ids.NewGuid(),
            OutboxWriter.UsageRequestCompleted,
            payload,
            at,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
