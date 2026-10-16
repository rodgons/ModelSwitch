namespace ModelSwitch.Evaluation.Domain.Runs;

/// <summary>A set of Evaluation Runs over the same Dataset Version, created together so their results can be placed side by side.</summary>
internal sealed class Comparison
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid DatasetId { get; init; }

    public int DatasetVersion { get; init; }

    public required string Name { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public Guid? CreatedByAdminId { get; init; }
}

/// <summary>The execution of one Dataset Version against exactly one Run Configuration (evaluation.md sections 4 to 6).</summary>
internal sealed class EvaluationRun
{
    public Guid Id { get; init; }

    public Guid ComparisonId { get; init; }

    public Guid TenantId { get; init; }

    public Guid DatasetId { get; init; }

    public int DatasetVersion { get; init; }

    /// <summary>The frozen Run Configuration with every reference resolved (target, Prompt Version, params, Evaluator, concurrency).</summary>
    public required string RunConfigurationJson { get; init; }

    /// <summary>Lower-case hex SHA-256 of the canonical Run Configuration.</summary>
    public required string ConfigSha256 { get; init; }

    /// <summary>The run's optional Estimated Cost cap (<c>maxEstimatedCost</c>), USD.</summary>
    public decimal? MaxEstimatedCost { get; init; }

    /// <summary>One of <see cref="EvaluationRunStatuses"/>.</summary>
    public required string Status { get; set; }

    public int CasesTotal { get; init; }

    public int CasesDone { get; set; }

    public int CasesFailed { get; set; }

    /// <summary>Computed only from stored Case Results; null until the run ends or a recomputation writes it.</summary>
    public string? AggregatesJson { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>A code from the closed set (e.g. <c>run_cost_cap_reached</c>) when the run failed.</summary>
    public string? Error { get; set; }
}

internal static class EvaluationRunStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static IReadOnlyList<string> All { get; } = [Queued, Running, Succeeded, Failed, Cancelled];
}

/// <summary>
/// The outcome, score, usage and latency of one Case within one Evaluation Run. <c>unique(run_id, case_id)</c> makes writing it
/// idempotent, so an interrupted run resumes by skipping the Cases it already has.
/// </summary>
internal sealed class CaseResult
{
    public Guid Id { get; init; }

    public Guid RunId { get; init; }

    public Guid CaseId { get; init; }

    /// <summary>One of <see cref="CaseResultStatuses"/>.</summary>
    public required string Status { get; init; }

    /// <summary>0 to 1; null for <c>errored</c> and <c>skipped</c> Cases.</summary>
    public decimal? Score { get; init; }

    public string? PredictedJson { get; init; }

    public string? OutputText { get; init; }

    public required string ScoreDetailsJson { get; init; }

    /// <summary>The candidate call's Inference Request.</summary>
    public Guid? InferenceRequestId { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public decimal? EstimatedCost { get; init; }

    /// <summary>The <c>llm_judge</c> call's Inference Request, when the Evaluator made one.</summary>
    public Guid? JudgeInferenceRequestId { get; init; }

    public int? JudgeInputTokens { get; init; }

    public int? JudgeOutputTokens { get; init; }

    public decimal? JudgeEstimatedCost { get; init; }

    public int? LatencyMs { get; init; }

    public string? ErrorCode { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

internal static class CaseResultStatuses
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Errored = "errored";
    public const string Skipped = "skipped";

    public static IReadOnlyList<string> All { get; } = [Passed, Failed, Errored, Skipped];
}
