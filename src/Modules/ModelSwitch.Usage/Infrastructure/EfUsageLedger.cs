using Microsoft.EntityFrameworkCore;
using ModelSwitch.SharedKernel;
using ModelSwitch.Usage.Contracts;
using ModelSwitch.Usage.Domain;

namespace ModelSwitch.Usage.Infrastructure;

/// <summary>
/// <see cref="IUsageLedger"/> over the <c>usage</c> schema. Open and settle are one transaction each. Never stores content
/// (ADR-0010): the contract carries none.
/// </summary>
internal sealed class EfUsageLedger(IDbContextFactory<UsageDbContext> contexts, TimeProvider time, IGuidGenerator ids) : IUsageLedger
{
    private const int _providerErrorTypeMaxLength = 64;

    public async Task<Result<OpenedRequest>> OpenAsync(OpenInferenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // timestamptz keeps microseconds; the value is also the WHERE key at settlement, so it must survive the round trip.
        var startedAt = TruncateToMicroseconds(time.GetUtcNow());

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.InferenceRequests.Add(new InferenceRequestRow
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
            Status = "running",

            // Until the request settles nothing is known, which is also what an abandoned request stays at.
            UsageStatus = "unavailable",
            TraceId = request.TraceId,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OpenedRequest(request.RequestId, startedAt);
    }

    public async Task SettleAsync(SettleInferenceRequest settlement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settlement);

        var completedAt = TruncateToMicroseconds(time.GetUtcNow());
        var summary = RequestSummary.From(settlement.Attempts);
        var status = Status(settlement.Outcome);
        var durationMs = ToMilliseconds(completedAt - settlement.StartedAt);

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

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
            // Already settled (or swept as abandoned): settling twice must not duplicate Attempts.
            return;
        }

        foreach (var attempt in settlement.Attempts)
        {
            db.ProviderAttempts.Add(new ProviderAttemptRow
            {
                Id = ids.NewGuid(),
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
                EstimatedCost = attempt.EstimatedCost?.Amount,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
        => new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    private static int ToMilliseconds(TimeSpan span) => (int)Math.Clamp(span.TotalMilliseconds, 0, int.MaxValue);

    private static string? Truncate(string? value, int maxLength) => value is { Length: var length } && length > maxLength ? value[..maxLength] : value;

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
