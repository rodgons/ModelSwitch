using System.Text.Json;
using ModelSwitch.Evaluation.Domain.Evaluators;

namespace ModelSwitch.Evaluation.Domain.Aggregates;

/// <summary>
/// The stored columns of one <c>case_result</c> that run aggregates are computed from (evaluation.md section 5: aggregates come
/// only from stored Case Results). <see cref="Expected"/> is the Case's <c>expected</c>, joined in by the reader. Token and cost
/// columns are the candidate call's; the <c>Judge*</c> ones are the <c>llm_judge</c> call's.
/// </summary>
internal sealed record CaseResultRecord(
    CaseStatus Status,
    decimal? Score,
    JsonElement? Predicted,
    JsonElement? Expected,
    JsonElement? ScoreDetails,
    int? LatencyMs,
    int? InputTokens,
    int? OutputTokens,
    decimal? EstimatedCost)
{
    public int? JudgeInputTokens { get; init; }

    public int? JudgeOutputTokens { get; init; }

    public decimal? JudgeEstimatedCost { get; init; }

    public string? ErrorCode { get; init; }

    /// <summary>The record a Case Result holds after the executor stores <paramref name="score"/> with its usage.</summary>
    public static CaseResultRecord From(
        CaseScore score,
        JsonElement? expected,
        int? latencyMs,
        int? inputTokens,
        int? outputTokens,
        decimal? estimatedCost)
    {
        ArgumentNullException.ThrowIfNull(score);
        return new CaseResultRecord(score.Status, score.Score, score.Predicted, expected, score.ScoreDetails, latencyMs, inputTokens, outputTokens, estimatedCost)
        {
            ErrorCode = score.ErrorCode,
        };
    }
}
