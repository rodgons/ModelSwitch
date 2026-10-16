using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelSwitch.Evaluation.Domain.Aggregates;
using ModelSwitch.Evaluation.Domain.Evaluators;
using ModelSwitch.Evaluation.Infrastructure;

namespace ModelSwitch.Evaluation.Application;

/// <summary>
/// A run's aggregates, computed only from its stored Case Results with each Case's <c>expected</c> joined in (evaluation.md section 5),
/// so the same numbers come back however often they are recomputed.
/// </summary>
internal static class RunAggregation
{
    public static async Task<RunAggregates> ComputeAsync(EvaluationDbContext db, Guid runId, string evaluatorKind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await (
                from result in db.CaseResults.AsNoTracking()
                join @case in db.Cases.AsNoTracking() on result.CaseId equals @case.Id
                where result.RunId == runId
                select new
                {
                    result.Status,
                    result.Score,
                    result.PredictedJson,
                    @case.ExpectedJson,
                    result.ScoreDetailsJson,
                    result.LatencyMs,
                    result.InputTokens,
                    result.OutputTokens,
                    result.EstimatedCost,
                    result.JudgeInputTokens,
                    result.JudgeOutputTokens,
                    result.JudgeEstimatedCost,
                    result.ErrorCode,
                })
            .ToListAsync(cancellationToken);

        var records = rows
            .Select(r => new CaseResultRecord(
                CaseStatusExtensions.Parse(r.Status),
                r.Score,
                Element(r.PredictedJson),
                Element(r.ExpectedJson),
                Element(r.ScoreDetailsJson),
                r.LatencyMs,
                r.InputTokens,
                r.OutputTokens,
                r.EstimatedCost)
            {
                JudgeInputTokens = r.JudgeInputTokens,
                JudgeOutputTokens = r.JudgeOutputTokens,
                JudgeEstimatedCost = r.JudgeEstimatedCost,
                ErrorCode = r.ErrorCode,
            })
            .ToList();
        return RunAggregateCalculator.Compute(evaluatorKind, records);
    }

    public static string ToJson(RunAggregates aggregates) => RunAggregateCalculator.ToJson(aggregates).GetRawText();

    private static JsonElement? Element(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
