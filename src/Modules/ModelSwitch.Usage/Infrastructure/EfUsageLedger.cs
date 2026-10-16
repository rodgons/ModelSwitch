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
/// <see cref="IUsageLedger"/> over the <c>usage</c> schema. Open and settle are one transaction each (architecture.md section 5,
/// steps 8 and 11). Never stores content (ADR-0010): the contract carries none.
/// </summary>
internal sealed class EfUsageLedger(IDbContextFactory<UsageDbContext> contexts, ICostCalculator calculator, UsageMetrics metrics, TimeProvider time, IGuidGenerator ids) : IUsageLedger
{
    private const int _providerErrorTypeMaxLength = 64;

    public async Task<Result<OpenedRequest>> OpenAsync(OpenInferenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // timestamptz keeps microseconds; the value is also the WHERE key at settlement, so it must survive the round trip.
        var startedAt = TruncateToMicroseconds(time.GetUtcNow());

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (request.Reservation is { } hold)
        {
            // Reserve and INSERT inference_request in one transaction: the period row is created lazily, the hold is a single
            // conditional UPDATE, and a refusal rolls everything back.
            var period = BudgetSql.PeriodOf(startedAt);
            await BudgetSql.EnsurePeriodAsync(db, request.TenantId, period, cancellationToken);
            if (hold.Amount.Amount > 0 && !await BudgetSql.TryHoldAsync(db, request.TenantId, period, hold.Amount.Amount, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                await RecordRefusedAsync(request, startedAt, cancellationToken);
                return new Error(ErrorCode.BudgetExceeded, "The Tenant's budget for this period does not cover this request.");
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
        }

        db.InferenceRequests.Add(NewRow(request, startedAt, "running"));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OpenedRequest(request.RequestId, startedAt);
    }

    public async Task<Result> TopUpAsync(Guid requestId, Money additional, CancellationToken cancellationToken)
    {
        if (additional.Amount <= 0)
        {
            return Result.Success();
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Lock the Reservation first; the period update below is the same conditional UPDATE as the reservation.
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

        if (!await BudgetSql.TryHoldAsync(db, tenantId, period, additional.Amount, cancellationToken))
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
            .Select(r => new { r.TenantId, r.RequestedAlias })
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

        // Release the Reservation and book the settled Estimated Cost. A request opened without a Reservation settles in the period it started in.
        await using (var release = BudgetSql.Command(
            db,
            "UPDATE usage.reservation SET status = 'settled', settled_at = @now WHERE inference_request_id = @id AND status = 'held' RETURNING amount, period_start",
            ("now", completedAt),
            ("id", settlement.RequestId)))
        {
            decimal held = 0;
            var period = BudgetSql.PeriodOf(settlement.StartedAt);
            await using (var reader = await release.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    held = reader.GetDecimal(0);
                    period = reader.GetFieldValue<DateOnly>(1);
                }
            }

            await BudgetSql.EnsurePeriodAsync(db, identity.TenantId, period, cancellationToken);
            await BudgetSql.SettleAsync(db, identity.TenantId, period, held, total, cancellationToken);
        }

        var payload = JsonSerializer.Serialize(new
        {
            requestId = settlement.RequestId,
            tenantId = identity.TenantId,
            requestedAlias = identity.RequestedAlias,
            status,
            usageStatus = summary.UsageStatus,
            selectedProviderModelId = summary.SelectedProviderModelId,
            attemptCount = summary.AttemptCount,
            hadFallback = summary.HadFallback,
            inputTokens = summary.TotalInputTokens,
            outputTokens = summary.TotalOutputTokens,
            estimatedCost = total,
            currency = Money.Usd,
            startedAt = settlement.StartedAt,
            durationMs,
            ttftMs = settlement.TtftMs,
        });
        await OutboxWriter.AppendAsync(
            db.Database.GetDbConnection(),
            db.Database.CurrentTransaction!.GetDbTransaction(),
            ids.NewGuid(),
            OutboxWriter.UsageRequestCompleted,
            payload,
            completedAt,
            cancellationToken);

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
