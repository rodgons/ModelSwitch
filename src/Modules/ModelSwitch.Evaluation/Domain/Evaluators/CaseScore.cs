using System.Text.Json;

namespace ModelSwitch.Evaluation.Domain.Evaluators;

/// <summary>The outcome of one Case (<c>case_result.status</c>).</summary>
internal enum CaseStatus
{
    /// <summary>Scored, and the score met the Evaluator's pass rule.</summary>
    Passed,

    /// <summary>Scored, and the score did not meet the pass rule. An unparseable output is failed with score 0, never dropped.</summary>
    Failed,

    /// <summary>Not scored: a Provider error, a template error or an unusable judge reply. Counted in the errored rate, not in quality.</summary>
    Errored,

    /// <summary>Never started, because the run's <c>maxEstimatedCost</c> was reached.</summary>
    Skipped,
}

internal static class CaseStatusExtensions
{
    /// <summary>The lowercase value stored in <c>case_result.status</c>.</summary>
    public static string ToWire(this CaseStatus status) => status switch
    {
        CaseStatus.Passed => "passed",
        CaseStatus.Failed => "failed",
        CaseStatus.Errored => "errored",
        CaseStatus.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static CaseStatus Parse(string value) => value switch
    {
        "passed" => CaseStatus.Passed,
        "failed" => CaseStatus.Failed,
        "errored" => CaseStatus.Errored,
        "skipped" => CaseStatus.Skipped,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown Case Result status."),
    };
}

/// <summary>What an Evaluator sees of one Case: its input, its expected result and the candidate's output text.</summary>
/// <remarks>Content (ADR-0010 makes Evaluation the stored exception): never log it.</remarks>
internal sealed record CaseContext(JsonElement Input, JsonElement Expected, string Output)
{
    public override string ToString() => "CaseContext { Input = <redacted>, Expected = <redacted>, Output = <redacted> }";
}

/// <summary>
/// The scoring part of a Case Result: <c>status</c>, <c>score</c> (0 to 1, null unless scored), <c>predicted</c>,
/// <c>score_details</c> (always a JSON object) and <c>error_code</c> (errored only).
/// </summary>
internal sealed record CaseScore(CaseStatus Status, decimal? Score, JsonElement? Predicted, JsonElement ScoreDetails, string? ErrorCode)
{
    /// <summary>Decimal places of <c>case_result.score numeric(6,5)</c>.</summary>
    public const int ScoreScale = 5;

    /// <summary>
    /// A scored Case. The score is rounded to <see cref="ScoreScale"/> places (banker's rounding) so that the value aggregates
    /// are computed from here is the one the database will hold.
    /// </summary>
    public static CaseScore Scored(decimal score, bool passed, JsonElement? predicted, object details)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(score, 0m);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(score, 1m);
        return new CaseScore(
            passed ? CaseStatus.Passed : CaseStatus.Failed,
            decimal.Round(score, ScoreScale, MidpointRounding.ToEven),
            predicted,
            EvaluationJson.ToElement(details),
            null);
    }

    /// <summary>An errored Case: no score. The details default to <c>{"error": message}</c>.</summary>
    public static CaseScore Errored(CaseError error, object? details = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new CaseScore(CaseStatus.Errored, null, null, EvaluationJson.ToElement(details ?? new { Error = error.Message }), error.Code);
    }

    /// <summary>A Case that never started because the run's cost cap was reached.</summary>
    public static CaseScore Skipped() => new(CaseStatus.Skipped, null, null, EvaluationJson.ToElement(new { }), null);
}
