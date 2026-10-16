using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ModelSwitch.Evaluation.Domain.Evaluators;
using ModelSwitch.Evaluation.Domain.Runs;
using ModelSwitch.Evaluation.Domain.Templates;
using ModelSwitch.Evaluation.Infrastructure;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Jobs;
using ModelSwitch.Usage.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace ModelSwitch.Evaluation.Application;

/// <summary>
/// Executes one Evaluation Run in the worker (evaluation.md sections 4 and 5, NFR-02):
/// <list type="number">
/// <item>A queued run becomes running; a running one (its previous worker died) resumes; a finished one is left alone.</item>
/// <item>The Cases without a Case Result are processed in upload order, <c>concurrency</c> at a time. Each one renders the Prompt
/// Version (a missing variable errors it without a call), calls <see cref="IInferenceService"/> (Request Origin <c>evaluation</c>),
/// scores the output, and for <c>llm_judge</c> makes the judge call. Its Case Result is written as soon as it is done (idempotent on
/// <c>(run_id, case_id)</c>), together with the run's progress counters.</item>
/// <item>Before each Case starts: the run must still be <c>running</c> (cancellation: in-flight Cases finish, nothing new starts, Cases
/// never started get no result), and the cost cap must allow it (<see cref="RunCostCap"/>; once it does not, the remaining Cases are
/// written <c>skipped</c> and the run fails with <c>run_cost_cap_reached</c>).</item>
/// <item>At the end the aggregates are computed from the stored Case Results only and stored with the final status.</item>
/// </list>
/// Logs carry ids and counts only, never Case content or model output (ADR-0010).
/// </summary>
internal sealed partial class RunExecutor(
    IDbContextFactory<EvaluationDbContext> contexts,
    IInferenceService inference,
    IEvaluationSpend spend,
    CaseCosting costing,
    IGuidGenerator ids,
    TimeProvider time,
    ILogger<RunExecutor> logger)
{
    private sealed record CaseInput(Guid Id, JsonElement Input, JsonElement Expected);

    /// <summary>Everything one finished Case writes to its Case Result.</summary>
    private sealed record CaseOutcome(CaseScore Score, string? OutputText, int? LatencyMs)
    {
        public Guid? RequestId { get; init; }

        public int? InputTokens { get; init; }

        public int? OutputTokens { get; init; }

        public decimal? Cost { get; init; }

        public Guid? JudgeRequestId { get; init; }

        public int? JudgeInputTokens { get; init; }

        public int? JudgeOutputTokens { get; init; }

        public decimal? JudgeCost { get; init; }

        public decimal TotalCost => (Cost ?? 0m) + (JudgeCost ?? 0m);
    }

    /// <summary>The run's spend while it executes, shared by the Cases in flight.</summary>
    private sealed class Spend(decimal spent)
    {
        private readonly Lock _lock = new();
        private decimal _spent = spent;
        private decimal _inFlight;

        public bool TryStart(decimal? cap, decimal worstCase)
        {
            lock (_lock)
            {
                if (!RunCostCap.AllowsNextCase(cap, _spent, _inFlight, worstCase))
                {
                    return false;
                }

                _inFlight += worstCase;
                return true;
            }
        }

        public void Finish(decimal worstCase, decimal cost)
        {
            lock (_lock)
            {
                _inFlight -= worstCase;
                _spent += cost;
            }
        }
    }

    public async Task ExecuteAsync(Guid runId, CancellationToken cancellationToken)
    {
        EvaluationRun? run;
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
        }

        if (run is null)
        {
            LogRunMissing(logger, runId);
            return;
        }

        switch (RunStatusRules.OnClaim(run.Status))
        {
            case RunStart.Skip:
                LogRunSkipped(logger, runId, run.Status);
                return;
            case RunStart.Start when !await StartAsync(runId, cancellationToken):
                var status = await StatusAsync(runId, cancellationToken);
                LogRunSkipped(logger, runId, status);
                return;
        }

        var configuration = RunConfiguration.Parse(run.RunConfigurationJson);
        var evaluator = EvaluatorRegistry.Create(configuration.Evaluator.Kind, configuration.Evaluator.Version, configuration.Evaluator.Config);
        var template = PromptTemplate.Parse(configuration.Prompt.Template);
        if (evaluator.IsFailure || template.IsFailure)
        {
            throw new PermanentJobFailureException("The run's frozen Evaluator or Prompt Version template is not valid.");
        }

        var (cases, pending, capAlreadyReached) = await LoadCasesAsync(run, cancellationToken);
        LogRunStarted(logger, runId, cases.Count, cases.Count - pending.Count);
        var stop = capAlreadyReached
            ? new StopReason(CostCapReached: true, Cancelled: false, StartedCount: 0)
            : await ProcessAsync(run, configuration, evaluator.Value, template.Value, pending, new Spend(await spend.RunEstimatedCostAsync(runId, cancellationToken)), cancellationToken);

        if (stop.CostCapReached)
        {
            foreach (var skipped in pending.Skip(stop.StartedCount))
            {
                await WriteAsync(runId, skipped.Id, new CaseOutcome(CaseScore.Skipped(), null, null), cancellationToken);
            }
        }

        await FinishAsync(runId, configuration.Evaluator.Kind, stop.CostCapReached, stop.Cancelled, cancellationToken);
    }

    /// <summary>Marks a run that cannot go on as failed with <paramref name="errorCode"/> (its aggregates from what was stored).</summary>
    public async Task FailAsync(Guid runId, string errorCode, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null || RunStatusRules.IsTerminal(run.Status))
        {
            return;
        }

        string? aggregates = null;
        try
        {
            aggregates = RunAggregation.ToJson(await RunAggregation.ComputeAsync(db, runId, RunConfiguration.Parse(run.RunConfigurationJson).Evaluator.Kind, cancellationToken));
        }
#pragma warning disable CA1031 // The run is failed whatever broke; aggregates can be recomputed later.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogAggregatesFailed(logger, exception, runId);
        }

        var now = time.GetUtcNow();
        await db.Database.ExecuteSqlAsync(
            $"UPDATE evaluation.evaluation_run SET status = 'failed', error = {errorCode}, aggregates = {aggregates}::jsonb, completed_at = {now} WHERE id = {runId} AND status IN ('queued', 'running')",
            cancellationToken);
        LogRunFinished(logger, runId, EvaluationRunStatuses.Failed, errorCode);
    }

    /// <summary>
    /// Stores the aggregates and <c>completed_at</c> of a run cancelled while running whose worker died before doing so
    /// (<see cref="RunStatusRules.NeedsCancelledFinalization"/>). It stays <c>cancelled</c>; the update is guarded on
    /// <c>completed_at is null</c>, so a worker that finishes it meanwhile keeps its own write.
    /// </summary>
    public async Task FinalizeCancelledAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null || run.Status != EvaluationRunStatuses.Cancelled || run.CompletedAt is not null)
        {
            return;
        }

        var aggregates = RunAggregation.ToJson(await RunAggregation.ComputeAsync(db, runId, RunConfiguration.Parse(run.RunConfigurationJson).Evaluator.Kind, cancellationToken));
        var now = time.GetUtcNow();
        await db.Database.ExecuteSqlAsync(
            $"UPDATE evaluation.evaluation_run SET aggregates = {aggregates}::jsonb, completed_at = {now} WHERE id = {runId} AND status = 'cancelled' AND completed_at IS NULL",
            cancellationToken);
        LogRunFinished(logger, runId, EvaluationRunStatuses.Cancelled, null);
    }

    private readonly record struct StopReason(bool CostCapReached, bool Cancelled, int StartedCount);

    private async Task<StopReason> ProcessAsync(
        EvaluationRun run,
        RunConfiguration configuration,
        IEvaluator evaluator,
        PromptTemplate template,
        IReadOnlyList<CaseInput> pending,
        Spend spent,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var slots = new SemaphoreSlim(configuration.Concurrency);
        var tasks = new List<Task>();
        var started = 0;
        bool capReached = false, cancelled = false;
        Exception? loopFailure = null;
        try
        {
            foreach (var @case in pending)
            {
                await slots.WaitAsync(linked.Token);
                if (!RunStatusRules.MayStartCase(await StatusAsync(run.Id, linked.Token)))
                {
                    slots.Release();
                    cancelled = true;
                    break;
                }

                var rendered = PromptRenderer.Render(template, @case.Input);
                var worst = run.MaxEstimatedCost is null
                    ? 0m
                    : (await costing.EstimateAsync(run.TenantId, run.Id, configuration, evaluator, rendered, @case.Input, @case.Expected, linked.Token)).Total;
                if (!spent.TryStart(run.MaxEstimatedCost, worst))
                {
                    slots.Release();
                    capReached = true;
                    break;
                }

                started++;
                tasks.Add(RunCaseAsync(run, configuration, evaluator, @case, rendered, worst, spent, slots, linked));
            }
        }
#pragma warning disable CA1031 // Rethrown below, once the Cases in flight have finished.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            loopFailure = exception;
        }

        var all = Task.WhenAll(tasks);
        try
        {
            await all;
        }
#pragma warning disable CA1031 // Inspected below.
        catch
#pragma warning restore CA1031
        {
        }

        if (all.Exception?.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException) is { } caseFailure)
        {
            ExceptionDispatchInfo.Throw(caseFailure);
        }

        if (loopFailure is not null)
        {
            ExceptionDispatchInfo.Throw(loopFailure);
        }

        await all;
        return new StopReason(capReached, cancelled, started);
    }

    private async Task RunCaseAsync(
        EvaluationRun run,
        RunConfiguration configuration,
        IEvaluator evaluator,
        CaseInput @case,
        RenderResult rendered,
        decimal worst,
        Spend spent,
        SemaphoreSlim slots,
        CancellationTokenSource linked)
    {
        // Yield first, so the loop can start the next Case while this one waits on the Provider.
        await Task.Yield();
        try
        {
            var outcome = await CaseAsync(run, configuration, evaluator, @case, rendered, linked.Token);
            await WriteAsync(run.Id, @case.Id, outcome, linked.Token);
            spent.Finish(worst, outcome.TotalCost);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Stop starting Cases; the job is retried and resumes from the stored Case Results.
            await linked.CancelAsync();
            throw;
        }
        finally
        {
            slots.Release();
        }
    }

    private async Task<CaseOutcome> CaseAsync(
        EvaluationRun run,
        RunConfiguration configuration,
        IEvaluator evaluator,
        CaseInput @case,
        RenderResult rendered,
        CancellationToken cancellationToken)
    {
        if (rendered.Prompt is not { } prompt)
        {
            return new CaseOutcome(CaseScore.Errored(rendered.Error!), null, null);
        }

        var requestId = ids.NewGuid();
        var started = time.GetTimestamp();
        var candidate = await inference.CompleteAsync(RunInference.Candidate(requestId, run.TenantId, run.Id, configuration, prompt), cancellationToken);
        if (candidate.IsFailure)
        {
            return new CaseOutcome(CaseScore.Errored(new CaseError(candidate.Error.Code.Value, "The candidate call failed.")), null, Milliseconds(time.GetElapsedTime(started)))
            {
                RequestId = requestId,
                Cost = await spend.RequestsEstimatedCostAsync([requestId], cancellationToken),
            };
        }

        var result = candidate.Value;
        var context = new CaseContext(@case.Input, @case.Expected, result.Text);
        var outcome = new CaseOutcome(default!, result.Text, Milliseconds(result.Latency))
        {
            RequestId = requestId,
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
            Cost = result.EstimatedCost?.Amount,
        };

        if (evaluator is ILocalEvaluator local)
        {
            return outcome with { Score = local.Score(context) };
        }

        var judged = (IJudgedEvaluator)evaluator;
        var judgeRequestId = ids.NewGuid();
        var judge = await inference.CompleteAsync(
            RunInference.Judge(judgeRequestId, run.TenantId, run.Id, configuration, judged.BuildJudgeMessages(context)),
            cancellationToken);
        if (judge.IsFailure)
        {
            return outcome with
            {
                Score = CaseScore.Errored(new CaseError(judge.Error.Code.Value, "The judge call failed."), new { ModelJudged = true, judged.JudgeAlias, Error = "The judge call failed." }),
                JudgeRequestId = judgeRequestId,
                JudgeCost = await spend.RequestsEstimatedCostAsync([judgeRequestId], cancellationToken),
            };
        }

        return outcome with
        {
            Score = judged.ScoreJudgeReply(context, judge.Value.Text),
            JudgeRequestId = judgeRequestId,
            JudgeInputTokens = judge.Value.InputTokens,
            JudgeOutputTokens = judge.Value.OutputTokens,
            JudgeCost = judge.Value.EstimatedCost?.Amount,
        };
    }

    /// <summary>
    /// Writes the Case Result and moves the run's counters in one statement. A result the run already has (a resumed Case, a lost
    /// lease) is left as it is and counts nothing.
    /// </summary>
    private async Task WriteAsync(Guid runId, Guid caseId, CaseOutcome outcome, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH inserted AS (
                INSERT INTO evaluation.case_result (id, run_id, case_id, status, score, predicted, output_text, score_details,
                    inference_request_id, input_tokens, output_tokens, estimated_cost,
                    judge_inference_request_id, judge_input_tokens, judge_output_tokens, judge_estimated_cost,
                    latency_ms, error_code, created_at)
                VALUES (@id, @run_id, @case_id, @status, @score, @predicted, @output_text, @score_details,
                    @request_id, @input_tokens, @output_tokens, @cost,
                    @judge_request_id, @judge_input_tokens, @judge_output_tokens, @judge_cost,
                    @latency_ms, @error_code, @created_at)
                ON CONFLICT (run_id, case_id) DO NOTHING
                RETURNING status)
            UPDATE evaluation.evaluation_run
            SET cases_done = cases_done + (SELECT count(*) FROM inserted),
                cases_failed = cases_failed + (SELECT count(*) FROM inserted WHERE status = ANY(@failed_statuses))
            WHERE id = @run_id
            """;
        var score = outcome.Score;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", ids.NewGuid());
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("case_id", caseId);
        command.Parameters.AddWithValue("status", score.Status.ToWire());
        command.Parameters.Add(Nullable("score", NpgsqlDbType.Numeric, score.Score));
        command.Parameters.Add(Nullable("predicted", NpgsqlDbType.Jsonb, score.Predicted?.GetRawText()));
        command.Parameters.Add(Nullable("output_text", NpgsqlDbType.Text, outcome.OutputText));
        command.Parameters.Add(new NpgsqlParameter("score_details", NpgsqlDbType.Jsonb) { Value = score.ScoreDetails.GetRawText() });
        command.Parameters.Add(Nullable("request_id", NpgsqlDbType.Uuid, outcome.RequestId));
        command.Parameters.Add(Nullable("input_tokens", NpgsqlDbType.Integer, outcome.InputTokens));
        command.Parameters.Add(Nullable("output_tokens", NpgsqlDbType.Integer, outcome.OutputTokens));
        command.Parameters.Add(Nullable("cost", NpgsqlDbType.Numeric, outcome.Cost));
        command.Parameters.Add(Nullable("judge_request_id", NpgsqlDbType.Uuid, outcome.JudgeRequestId));
        command.Parameters.Add(Nullable("judge_input_tokens", NpgsqlDbType.Integer, outcome.JudgeInputTokens));
        command.Parameters.Add(Nullable("judge_output_tokens", NpgsqlDbType.Integer, outcome.JudgeOutputTokens));
        command.Parameters.Add(Nullable("judge_cost", NpgsqlDbType.Numeric, outcome.JudgeCost));
        command.Parameters.Add(Nullable("latency_ms", NpgsqlDbType.Integer, outcome.LatencyMs));
        command.Parameters.Add(Nullable("error_code", NpgsqlDbType.Text, score.ErrorCode));
        command.Parameters.AddWithValue("created_at", time.GetUtcNow());
        command.Parameters.Add(new NpgsqlParameter("failed_statuses", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = CaseResultStatuses.All.Where(RunStatusRules.CountsAsFailed).ToArray(),
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task FinishAsync(Guid runId, string evaluatorKind, bool costCapReached, bool cancelled, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var computed = await RunAggregation.ComputeAsync(db, runId, evaluatorKind, cancellationToken);
        var aggregates = RunAggregation.ToJson(computed);
        var (status, error) = RunStatusRules.Outcome(costCapReached, cancelled, computed.Skipped);
        var now = time.GetUtcNow();
        var updated = cancelled
            ? 0
            : await db.Database.ExecuteSqlAsync(
                $"UPDATE evaluation.evaluation_run SET status = {status}, error = {error}, aggregates = {aggregates}::jsonb, completed_at = {now} WHERE id = {runId} AND status = 'running'",
                cancellationToken);
        if (updated == 0)
        {
            // Cancelled while the last Cases ran: it stays cancelled, with aggregates over what was stored.
            status = EvaluationRunStatuses.Cancelled;
            error = null;
            await db.Database.ExecuteSqlAsync(
                $"UPDATE evaluation.evaluation_run SET aggregates = {aggregates}::jsonb, completed_at = {now} WHERE id = {runId} AND status = 'cancelled'",
                cancellationToken);
        }

        LogRunFinished(logger, runId, status, error);
    }

    private async Task<bool> StartAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var now = time.GetUtcNow();
        return await db.Database.ExecuteSqlAsync(
            $"UPDATE evaluation.evaluation_run SET status = 'running', started_at = coalesce(started_at, {now}) WHERE id = {runId} AND status = 'queued'",
            cancellationToken) == 1;
    }

    private async Task<string> StatusAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Runs.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Status).FirstAsync(cancellationToken);
    }

    private async Task<(IReadOnlyList<CaseInput> All, IReadOnlyList<CaseInput> Pending, bool CapAlreadyReached)> LoadCasesAsync(EvaluationRun run, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.Cases.AsNoTracking()
            .Where(c => c.DatasetId == run.DatasetId && c.DatasetVersion == run.DatasetVersion)
            .OrderBy(c => c.Ordinal)
            .Select(c => new { c.Id, c.InputJson, c.ExpectedJson })
            .ToListAsync(cancellationToken);
        var stored = await db.CaseResults.AsNoTracking().Where(r => r.RunId == run.Id).Select(r => new { r.CaseId, r.Status }).ToListAsync(cancellationToken);
        var done = stored.Select(r => r.CaseId).ToHashSet();
        var all = rows.Select(r => new CaseInput(r.Id, Element(r.InputJson), Element(r.ExpectedJson))).ToList();
        var byId = all.ToDictionary(c => c.Id);
        return (all, [.. RunResume.Pending([.. all.Select(c => c.Id)], done).Select(id => byId[id])], RunResume.CostCapAlreadyReached(stored.Select(r => r.Status)));
    }

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static int Milliseconds(TimeSpan elapsed) => (int)Math.Min(int.MaxValue, Math.Round(elapsed.TotalMilliseconds));

    private static NpgsqlParameter Nullable(string name, NpgsqlDbType type, object? value)
        => new(name, type) { Value = value ?? DBNull.Value };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Evaluation Run {RunId} no longer exists; its job does nothing.")]
    private static partial void LogRunMissing(ILogger logger, Guid runId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Evaluation Run {RunId} is {Status}; its job does nothing.")]
    private static partial void LogRunSkipped(ILogger logger, Guid runId, string status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Evaluation Run {RunId} started: {Cases} Case(s), {Done} already recorded.")]
    private static partial void LogRunStarted(ILogger logger, Guid runId, int cases, int done);

    [LoggerMessage(Level = LogLevel.Information, Message = "Evaluation Run {RunId} finished {Status}, error {Error}.")]
    private static partial void LogRunFinished(ILogger logger, Guid runId, string status, string? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Computing the aggregates of Evaluation Run {RunId} failed.")]
    private static partial void LogAggregatesFailed(ILogger logger, Exception exception, Guid runId);
}
