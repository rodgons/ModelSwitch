using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Outbox;
using ModelSwitch.Usage.Contracts;
using ModelSwitch.Usage.Domain;

namespace ModelSwitch.Usage.Infrastructure;

/// <summary>
/// <see cref="IUsageLedger"/> over the <c>usage</c> schema. Open and settle are one transaction each (architecture.md section 5,
/// steps 8 and 11). Never stores content (ADR-0010): the contract carries none.
/// </summary>
internal sealed class EfUsageLedger(IDbContextFactory<UsageDbContext> contexts, ICostCalculator calculator, UsageMetrics metrics, TimeProvider time, IGuidGenerator ids) : IUsageLedger
{
    private const int _providerErrorTypeMaxLength = 64;

    // The hold is the last statement before commit, so the period row (Tenants with a Budget only) is locked for one round trip.
    // It also reports whether the period row exists and whether the Tenant has a Budget, which tells a refusal from "no Budget".
    private const string _holdSql =
        """
        WITH hold AS (
            UPDATE usage.budget_period
               SET reserved_amount = reserved_amount + @amount
             WHERE tenant_id = @tenant AND period_start = @period AND @amount > 0
               AND (limit_amount IS NULL OR spent_amount + reserved_amount + @amount <= limit_amount)
            RETURNING 1)
        SELECT EXISTS (SELECT 1 FROM hold),
               EXISTS (SELECT 1 FROM usage.budget_period WHERE tenant_id = @tenant AND period_start = @period),
               EXISTS (SELECT 1 FROM usage.budget WHERE tenant_id = @tenant)
        """;

    // Releases the Reservation and books the settled cost in one statement; it runs after the Budget lock, so it sees a period row
    // created before the lock was granted. Without a row (no Budget) it changes nothing on budget_period.
    private const string _releaseSql =
        """
        WITH released AS (
            UPDATE usage.reservation SET status = 'settled', settled_at = @now
             WHERE inference_request_id = @id AND status = 'held'
            RETURNING amount)
        UPDATE usage.budget_period
           SET reserved_amount = reserved_amount - coalesce((SELECT amount FROM released), 0),
               spent_amount = spent_amount + @settled
         WHERE tenant_id = @tenant AND period_start = @period
        """;

    private enum HoldOutcome
    {
        Held,
        NoBudget,
        Refused,
        PeriodMissing,
    }

    public async Task<Result<OpenedRequest>> OpenAsync(OpenInferenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // timestamptz keeps microseconds; the value is also the WHERE key at settlement, so it must survive the round trip.
        var startedAt = TruncateToMicroseconds(time.GetUtcNow());
        var period = BudgetSql.PeriodOf(startedAt);

        // At most two passes: the first Reservation of a month for a Tenant with a Budget creates the period row, then reserves again.
        for (var pass = 0; ; pass++)
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            db.InferenceRequests.Add(NewRow(request, startedAt, "running"));
            if (request.Reservation is not { } hold)
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new OpenedRequest(request.RequestId, startedAt);
            }

            db.Reservations.Add(new ReservationRow
            {
                Id = ids.NewGuid(),
                InferenceRequestId = request.RequestId,
                TenantId = request.TenantId,
                PeriodStart = period,
                Amount = hold.Amount.Amount,
                Status = "held",
                ExpiresAt = TruncateToMicroseconds(hold.ExpiresAt),
                CreatedAt = startedAt,
            });
            await db.SaveChangesAsync(cancellationToken);

            var outcome = await HoldAsync(db, request.TenantId, period, hold.Amount.Amount, cancellationToken);
            if (outcome is HoldOutcome.Held or HoldOutcome.NoBudget || (outcome is HoldOutcome.PeriodMissing && pass > 0))
            {
                await transaction.CommitAsync(cancellationToken);
                return new OpenedRequest(request.RequestId, startedAt);
            }

            await transaction.RollbackAsync(cancellationToken);
            if (outcome is HoldOutcome.Refused)
            {
                await RecordRefusedAsync(request, startedAt, cancellationToken);
                return new Error(ErrorCode.BudgetExceeded, "The Tenant's budget for this period does not cover this request.");
            }

            await BudgetSql.SeedPeriodAsync(db, request.TenantId, period, cancellationToken);
        }
    }

    private static async Task<HoldOutcome> HoldAsync(UsageDbContext db, Guid tenantId, DateOnly period, decimal amount, CancellationToken cancellationToken)
    {
        await using var batch = BudgetSql.Batch(
            db,
            (BudgetSql.SharedLockSql, BudgetSql.LockParameters(tenantId)),
            (_holdSql, [("tenant", tenantId), ("period", period), ("amount", amount)]));
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var (held, periodExists, hasBudget) = (reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2));
        return held ? HoldOutcome.Held
            : periodExists ? (amount > 0 ? HoldOutcome.Refused : HoldOutcome.Held)
            : hasBudget ? HoldOutcome.PeriodMissing
            : HoldOutcome.NoBudget;
    }

    public async Task<Result> TopUpAsync(Guid requestId, Money additional, CancellationToken cancellationToken)
    {
        if (additional.Amount <= 0)
        {
            return Result.Success();
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Raise the Reservation first; the period update below is the same conditional UPDATE as the reservation.
        await using var raise = BudgetSql.Command(
            db,
            "UPDATE usage.reservation SET amount = amount + @amount WHERE inference_request_id = @id AND status = 'held' RETURNING tenant_id, period_start",
            ("amount", additional.Amount),
            ("id", requestId));
        Guid tenantId;
        DateOnly period;
        await using (var reader = await raise.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                // Nothing held: the request was already settled or swept. That is a bug in the caller, not a budget decision.
                return new Error(ErrorCode.InternalError, "The request holds no Reservation to top up.");
            }

            tenantId = reader.GetGuid(0);
            period = reader.GetFieldValue<DateOnly>(1);
        }

        // With a period row the top-up is checked like the Reservation (a row seeded after this request opened counts its hold
        // already). Without one the Tenant has no Budget for the month and nothing is checked; a row seeded later counts the new amount.
        if (await HoldAsync(db, tenantId, period, additional.Amount, cancellationToken) is HoldOutcome.Refused)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new Error(ErrorCode.BudgetExceeded, "The Tenant's budget for this period does not cover another Attempt.");
        }

        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }

    public async Task SettleAsync(SettleInferenceRequest settlement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settlement);

        var now = time.GetUtcNow();
        var completedAt = TruncateToMicroseconds(now);
        var maxOutput = settlement.MaxOutputTokens is > 0 ? settlement.MaxOutputTokens.Value : ICostCalculator.DefaultMaxOutputTokens;

        // Cost every Attempt first: the totals on the request and the budget movement are sums of the Usage Records.
        var drafts = settlement.Attempts.Select(a => AttemptCosting.Cost(a, maxOutput, calculator)).ToList();
        var costed = settlement.Attempts
            .Select((a, i) => drafts[i] is { } draft ? a with { EstimatedCost = draft.TotalCost } : a)
            .ToList();
        var summary = RequestSummary.From(costed);
        var status = Status(settlement.Outcome);
        var durationMs = ToMilliseconds(completedAt - settlement.StartedAt);

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var identity = await db.InferenceRequests
            .Where(r => r.Id == settlement.RequestId && r.StartedAt == settlement.StartedAt && r.Status == "running")
            .Select(r => new { r.TenantId, r.RequestedAlias, r.Origin })
            .FirstOrDefaultAsync(cancellationToken);
        if (identity is null)
        {
            // Already settled (or swept as abandoned): settling twice must not duplicate Attempts.
            return;
        }

        var updated = await db.InferenceRequests
            .Where(r => r.Id == settlement.RequestId && r.StartedAt == settlement.StartedAt && r.Status == "running")
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(r => r.Status, status)
                    .SetProperty(r => r.ErrorCode, settlement.ErrorCode)
                    .SetProperty(r => r.PolicyId, settlement.PolicyId)
                    .SetProperty(r => r.PolicyVersion, settlement.PolicyVersion)
                    .SetProperty(r => r.RoutingDecision, settlement.RoutingDecisionJson)
                    .SetProperty(r => r.SelectedProviderModelId, summary.SelectedProviderModelId)
                    .SetProperty(r => r.AttemptCount, (short)summary.AttemptCount)
                    .SetProperty(r => r.HadFallback, summary.HadFallback)
                    .SetProperty(r => r.CompletedAt, completedAt)
                    .SetProperty(r => r.DurationMs, durationMs)
                    .SetProperty(r => r.TtftMs, settlement.TtftMs)
                    .SetProperty(r => r.TotalInputTokens, summary.TotalInputTokens)
                    .SetProperty(r => r.TotalOutputTokens, summary.TotalOutputTokens)
                    .SetProperty(r => r.TotalEstimatedCost, summary.TotalEstimatedCost == null ? (decimal?)null : summary.TotalEstimatedCost.Value.Amount)
                    .SetProperty(r => r.UsageStatus, summary.UsageStatus),
                cancellationToken);
        if (updated == 0)
        {
            return;
        }

        var total = 0m;
        for (var i = 0; i < settlement.Attempts.Count; i++)
        {
            var attempt = settlement.Attempts[i];
            var attemptId = ids.NewGuid();
            db.ProviderAttempts.Add(new ProviderAttemptRow
            {
                Id = attemptId,
                InferenceRequestId = settlement.RequestId,
                AttemptNumber = (short)attempt.AttemptNumber,
                ProviderId = attempt.ProviderId,
                ProviderModelId = attempt.ProviderModelId,
                Kind = Kind(attempt.Kind),
                Status = Status(attempt.Status),
                ErrorClass = attempt.ErrorClass is { } errorClass ? ErrorClassName(errorClass) : null,
                ErrorCode = attempt.ErrorCode,
                HttpStatus = (short?)attempt.HttpStatus,
                ProviderErrorType = Truncate(attempt.ProviderErrorType, _providerErrorTypeMaxLength),
                StartedAt = TruncateToMicroseconds(attempt.StartedAt),
                CompletedAt = TruncateToMicroseconds(attempt.CompletedAt),
                DurationMs = ToMilliseconds(attempt.CompletedAt - attempt.StartedAt),
                TtftMs = attempt.TtftMs,
                InputTokens = attempt.InputTokens,
                OutputTokens = attempt.OutputTokens,
                EstimatedCost = costed[i].EstimatedCost?.Amount,
            });

            if (drafts[i] is { } draft)
            {
                total += draft.TotalCost.Amount;
                db.UsageRecords.Add(new UsageRecordRow
                {
                    Id = ids.NewGuid(),
                    InferenceRequestId = settlement.RequestId,
                    ProviderAttemptId = attemptId,
                    TenantId = identity.TenantId,
                    ProviderModelId = attempt.ProviderModelId,
                    ModelPriceId = draft.ModelPriceId,
                    InputTokens = draft.InputTokens,
                    CachedInputTokens = draft.CachedInputTokens,
                    OutputTokens = draft.OutputTokens,
                    UsageSource = SourceName(draft.Source),
                    EstimateMethod = draft.EstimateMethod,
                    InputCost = draft.InputCost.Amount,
                    OutputCost = draft.OutputCost.Amount,
                    TotalCost = draft.TotalCost.Amount,
                    Currency = Money.Usd,
                    RecordedAt = completedAt,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        var attributedModel = summary.SelectedProviderModelId
            ?? settlement.Attempts.LastOrDefault(a => a.Status != AttemptStatus.SkippedCircuitOpen)?.ProviderModelId;
        var cached = drafts.Sum(d => (long?)d?.CachedInputTokens ?? 0);
        var payload = new RequestCompletedEvent(
            settlement.RequestId,
            identity.TenantId,
            identity.RequestedAlias,
            status,
            summary.UsageStatus,
            attributedModel,
            summary.SelectedProviderModelId,
            summary.AttemptCount,
            summary.HadFallback,
            summary.TotalInputTokens,
            cached > 0 ? (int)Math.Min(cached, int.MaxValue) : null,
            summary.TotalOutputTokens,
            total,
            Money.Usd,
            settlement.StartedAt,
            durationMs,
            settlement.TtftMs,
            identity.Origin).ToJson();
        await OutboxWriter.AppendAsync(
            db.Database.GetDbConnection(),
            db.Database.CurrentTransaction!.GetDbTransaction(),
            ids.NewGuid(),
            OutboxWriter.UsageRequestCompleted,
            payload,
            completedAt,
            cancellationToken);

        // Last, so the period row of a Tenant with a Budget stays locked only until the commit.
        await using (var release = BudgetSql.Batch(
            db,
            (BudgetSql.SharedLockSql, BudgetSql.LockParameters(identity.TenantId)),
            (_releaseSql, [("now", completedAt), ("id", settlement.RequestId), ("settled", total), ("tenant", identity.TenantId), ("period", BudgetSql.PeriodOf(settlement.StartedAt))])))
        {
            await release.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        for (var i = 0; i < drafts.Count; i++)
        {
            if (drafts[i] is { } draft)
            {
                metrics.Recorded(identity.RequestedAlias, settlement.Attempts[i].ProviderModelId, draft);
            }
        }
    }

    // A refused request is still a request: it is recorded as failed, with no Attempts and no hold.
    private async Task RecordRefusedAsync(OpenInferenceRequest request, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = NewRow(request, startedAt, "failed", ErrorCode.BudgetExceeded.Value, completed: true);
        db.InferenceRequests.Add(row);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static InferenceRequestRow NewRow(OpenInferenceRequest request, DateTimeOffset startedAt, string status, string? errorCode = null, bool completed = false)
        => new()
        {
            Id = request.RequestId,
            StartedAt = startedAt,
            TenantId = request.TenantId,
            Origin = request.Origin == RequestOrigin.Api ? "api" : "evaluation",
            ApiKeyId = request.ApiKeyId,
            EvaluationRunId = request.EvaluationRunId,
            ClientRequestId = request.ClientRequestId,
            RequestedAlias = request.RequestedAlias,
            TaskType = request.TaskType,
            Streaming = request.Streaming,
            InputBytes = request.InputBytes,
            Status = status,
            ErrorCode = errorCode,
            CompletedAt = completed ? startedAt : null,
            DurationMs = completed ? 0 : null,

            // Until the request settles nothing is known, which is also what an abandoned request stays at. A refused request has no Estimated Cost.
            UsageStatus = completed ? "reported" : "unavailable",
            TraceId = request.TraceId,
        };

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
        => new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    private static int ToMilliseconds(TimeSpan span) => (int)Math.Clamp(span.TotalMilliseconds, 0, int.MaxValue);

    private static string? Truncate(string? value, int maxLength) => value is { Length: var length } && length > maxLength ? value[..maxLength] : value;

    private static string SourceName(UsageSource source) => source switch
    {
        UsageSource.Reported => "reported",
        UsageSource.Estimated => "estimated",
        UsageSource.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    private static string Status(RequestOutcome outcome) => outcome switch
    {
        RequestOutcome.Succeeded => "succeeded",
        RequestOutcome.Failed => "failed",
        RequestOutcome.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    private static string Status(AttemptStatus status) => status switch
    {
        AttemptStatus.Succeeded => "succeeded",
        AttemptStatus.Failed => "failed",
        AttemptStatus.Cancelled => "cancelled",
        AttemptStatus.Ambiguous => "ambiguous",
        AttemptStatus.SkippedCircuitOpen => "skipped_circuit_open",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static string Kind(AttemptKind kind) => kind switch
    {
        AttemptKind.Primary => "primary",
        AttemptKind.Retry => "retry",
        AttemptKind.Fallback => "fallback",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string ErrorClassName(AttemptErrorClass errorClass) => errorClass switch
    {
        AttemptErrorClass.Transient => "transient",
        AttemptErrorClass.Permanent => "permanent",
        AttemptErrorClass.Ambiguous => "ambiguous",
        _ => throw new ArgumentOutOfRangeException(nameof(errorClass), errorClass, null),
    };
}
