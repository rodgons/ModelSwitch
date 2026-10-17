using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ModelSwitch.Evaluation.Domain;
using ModelSwitch.Evaluation.Domain.Aggregates;
using ModelSwitch.Evaluation.Domain.Evaluators;
using ModelSwitch.Evaluation.Domain.Runs;
using ModelSwitch.Evaluation.Domain.Templates;
using ModelSwitch.Evaluation.Infrastructure;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Jobs;

namespace ModelSwitch.Evaluation.Application;

/// <summary>
/// Creating, costing, cancelling and re-running Evaluation Runs (evaluation.md sections 4 and 5, FR-09). Operators write inside their
/// Tenant Grant; anything of a Tenant outside it is 404. Creating a Comparison writes it, one run per Run Configuration, one
/// <c>evaluation.run</c> job per run and the Audit Record in one transaction, so a run exists exactly when its job does.
/// </summary>
internal sealed class EvaluationRunService(
    IDbContextFactory<EvaluationDbContext> contexts,
    EvaluationAccess access,
    RunPlanner planner,
    CaseCosting costing,
    IJobEnqueuer jobs,
    IAuditWriter audit,
    IGuidGenerator ids,
    TimeProvider time)
{
    /// <summary>The suffix "Run again" adds to the source Comparison's name.</summary>
    public const string RunAgainSuffix = " (run again)";

    public async Task<Result<CreatedEvaluationView>> CreateAsync(CreateEvaluationRequest request, CancellationToken cancellationToken)
    {
        var plan = await planner.PlanAsync(request, cancellationToken);
        return plan.IsFailure ? plan.Error : await CreateComparisonAsync(plan.Value, rerunOf: null, cancellationToken);
    }

    /// <summary>
    /// The run builder's worst-case estimate (evaluation.md section 4): for each configuration, the sum over its Cases of each Case's
    /// worst case (<see cref="CaseWorstCase"/>). Validates exactly like <see cref="CreateAsync"/> and writes nothing.
    /// </summary>
    public async Task<Result<EvaluationEstimateView>> EstimateAsync(CreateEvaluationRequest request, CancellationToken cancellationToken)
    {
        var plan = await planner.PlanAsync(request, cancellationToken);
        if (plan.IsFailure)
        {
            return plan.Error;
        }

        var run = plan.Value;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var cases = await db.Cases.AsNoTracking()
            .Where(c => c.DatasetId == run.DatasetId && c.DatasetVersion == run.DatasetVersion)
            .OrderBy(c => c.Ordinal)
            .Select(c => new { c.InputJson, c.ExpectedJson })
            .ToListAsync(cancellationToken);
        var parsed = cases.Select(c => (Input: Parse(c.InputJson), Expected: Parse(c.ExpectedJson))).ToList();

        var views = new List<ConfigurationEstimateView>(run.Configurations.Count);
        for (var i = 0; i < run.Configurations.Count; i++)
        {
            var configuration = run.Configurations[i];
            var evaluator = EvaluatorRegistry.Create(configuration.Evaluator.Kind, configuration.Evaluator.Version, configuration.Evaluator.Config).Value;
            var template = PromptTemplate.Parse(configuration.Prompt.Template).Value;
            decimal candidate = 0m, judge = 0m;
            decimal? max = null;
            var estimated = 0;
            var failures = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var (input, expected) in parsed)
            {
                var worst = await costing.EstimateAsync(run.TenantId, Guid.Empty, configuration, evaluator, PromptRenderer.Render(template, input), input, expected, cancellationToken);
                candidate += worst.Candidate;
                judge += worst.Judge;
                if (worst.ErrorCode is { } code)
                {
                    failures[code] = failures.GetValueOrDefault(code) + 1;
                    continue;
                }

                estimated++;
                max = max is { } m && m >= worst.Total ? m : worst.Total;
            }

            var total = candidate + judge;
            views.Add(new ConfigurationEstimateView(
                i,
                configuration.Target.Label,
                parsed.Count,
                estimated,
                candidate,
                judge,
                total,
                max,
                run.MaxEstimatedCost is { } cap ? total > cap : null,
                failures));
        }

        return new EvaluationEstimateView(Money.Usd, views.Sum(v => v.WorstCase), views);
    }

    /// <summary>
    /// "Run again" (FR-09): a new Comparison with one run whose Run Configuration is a copy of the source run's frozen one (the same
    /// Policy Versions, Prompt Version, parameters and Evaluator, so the same <c>config_sha256</c>), recorded under the current
    /// gateway build. The source run may be in any state.
    /// </summary>
    public async Task<Result<CreatedEvaluationView>> RunAgainAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var source = await FindRunAsync(db, runId, cancellationToken);
        if (source.IsFailure)
        {
            return source.Error;
        }

        var run = source.Value;
        var comparison = await db.Comparisons.AsNoTracking().FirstAsync(c => c.Id == run.ComparisonId, cancellationToken);
        var casesTotal = await db.DatasetVersions.AsNoTracking()
            .Where(v => v.DatasetId == run.DatasetId && v.Version == run.DatasetVersion)
            .Select(v => v.CaseCount)
            .FirstAsync(cancellationToken);
        var configuration = RunConfiguration.Parse(run.RunConfigurationJson) with { GatewayVersion = GatewayBuild.Version };
        var name = comparison.Name.Length + RunAgainSuffix.Length <= ResourceNames.MaxLength
            ? comparison.Name + RunAgainSuffix
            : comparison.Name[..(ResourceNames.MaxLength - RunAgainSuffix.Length)] + RunAgainSuffix;
        var plan = new PlannedRun(run.TenantId, run.DatasetId, run.DatasetVersion, name, casesTotal, [configuration], run.MaxEstimatedCost);
        return await CreateComparisonAsync(plan, rerunOf: run.Id, cancellationToken);
    }

    /// <summary>
    /// Cancels a queued or running run (409 <c>conflict</c> once it has finished). Cases in flight finish and are recorded; no new Case
    /// starts, and Cases that never started get no Case Result. A queued run never starts, and gets its (empty) aggregates now; a
    /// running one gets them from the worker when its in-flight Cases are done.
    /// </summary>
    public async Task<Result<CancelledRunView>> CancelAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var found = await FindRunAsync(db, runId, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var run = found.Value;
        if (!RunStatusRules.CanCancel(run.Status))
        {
            return AlreadyFinished(run.Status);
        }

        var now = time.GetUtcNow();
        var kind = RunConfiguration.Parse(run.RunConfigurationJson).Evaluator.Kind;
        var empty = RunAggregation.ToJson(RunAggregateCalculator.Compute(kind, []));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var updated = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE evaluation.evaluation_run
            SET aggregates = CASE WHEN status = 'queued' THEN {empty}::jsonb ELSE aggregates END,
                completed_at = CASE WHEN status = 'queued' THEN {now} ELSE completed_at END,
                status = 'cancelled'
            WHERE id = {runId} AND status IN ('queued', 'running')
            """,
            cancellationToken);
        if (updated == 0)
        {
            return AlreadyFinished(run.Status);
        }

        var entry = new AuditEntry(EvaluationAuditActions.EvaluationCancelled, "evaluation_run", runId.ToString("D", CultureInfo.InvariantCulture))
        {
            TenantId = run.TenantId,
            Before = new Dictionary<string, object?> { ["status"] = run.Status },
            After = new Dictionary<string, object?> { ["status"] = EvaluationRunStatuses.Cancelled, ["comparisonId"] = run.ComparisonId },
        };
        await audit.WriteAsync(entry, transaction.GetDbTransaction(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CancelledRunView(runId, EvaluationRunStatuses.Cancelled);
    }

    /// <summary>
    /// Recomputes a finished run's aggregates from its stored Case Results (FR-09) and stores them. A queued or running run is 409
    /// <c>conflict</c>: its detail already shows live aggregates, and the worker stores the final ones.
    /// </summary>
    public async Task<Result<JsonNode?>> RecomputeAggregatesAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var found = await FindRunAsync(db, runId, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        if (!RunStatusRules.IsTerminal(found.Value.Status))
        {
            return new Error(ErrorCode.Conflict, "The run has not finished; its aggregates are computed when it does.");
        }

        var kind = RunConfiguration.Parse(found.Value.RunConfigurationJson).Evaluator.Kind;
        var json = RunAggregation.ToJson(await RunAggregation.ComputeAsync(db, runId, kind, cancellationToken));
        await db.Database.ExecuteSqlAsync($"UPDATE evaluation.evaluation_run SET aggregates = {json}::jsonb WHERE id = {runId}", cancellationToken);
        return JsonNode.Parse(json);
    }

    /// <summary>
    /// Deletes a Comparison with its Evaluation Runs and their Case Results, model outputs and copied templates included (evaluation.md
    /// section 8): 404 outside the grant, 409 <c>conflict</c> while any run is queued or running, or cancelled but still finishing its
    /// in-flight Cases. The runs are locked first, so the worker cannot start one between the check and the delete. The Audit Record
    /// keeps the name, Dataset Version, run ids and counts, never content.
    /// </summary>
    public async Task<Result> DeleteComparisonAsync(Guid comparisonId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var comparison = await db.Comparisons.AsNoTracking().FirstOrDefaultAsync(c => c.Id == comparisonId, cancellationToken);
        if (comparison is null || access.CheckTenant(comparison.TenantId).IsFailure)
        {
            return ComparisonNotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var locked = await db.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM evaluation.comparison WHERE id = {comparisonId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        if (locked.Count == 0)
        {
            return ComparisonNotFound();
        }

        var runs = await db.Database
            .SqlQuery<LockedRun>($"SELECT id AS \"Id\", status AS \"Status\", completed_at IS NOT NULL AS \"HasCompletedAt\" FROM evaluation.evaluation_run WHERE comparison_id = {comparisonId} ORDER BY id FOR UPDATE")
            .ToListAsync(cancellationToken);
        if (!DeletionRules.CanDeleteComparison(runs.Select(r => new RunState(r.Status, r.HasCompletedAt))))
        {
            return new Error(ErrorCode.Conflict, "A run of this comparison has not finished; cancel it and wait for it to stop first.");
        }

        var runIds = runs.Select(r => r.Id).ToArray();
        var caseResults = await db.CaseResults.CountAsync(r => runIds.Contains(r.RunId), cancellationToken);
        await db.Database.ExecuteSqlAsync($"DELETE FROM evaluation.comparison WHERE id = {comparisonId}", cancellationToken);

        var entry = new AuditEntry(EvaluationAuditActions.EvaluationDeleted, "comparison", comparisonId.ToString("D", CultureInfo.InvariantCulture))
        {
            TenantId = comparison.TenantId,
            Before = new Dictionary<string, object?>
            {
                ["name"] = comparison.Name,
                ["datasetId"] = comparison.DatasetId,
                ["datasetVersion"] = comparison.DatasetVersion,
                ["runIds"] = runIds,
                ["caseResults"] = caseResults,
            },
        };
        await audit.WriteAsync(entry, transaction.GetDbTransaction(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>The run when it exists and its Tenant is in the caller's grant; otherwise the same 404 either way.</summary>
    internal async Task<Result<EvaluationRun>> FindRunAsync(EvaluationDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
        return run is null || access.CheckTenant(run.TenantId).IsFailure
            ? new Error(ErrorCode.NotFound, "The evaluation run was not found.")
            : run;
    }

    private async Task<Result<CreatedEvaluationView>> CreateComparisonAsync(PlannedRun plan, Guid? rerunOf, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var comparison = new Comparison
        {
            Id = ids.NewGuid(),
            TenantId = plan.TenantId,
            DatasetId = plan.DatasetId,
            DatasetVersion = plan.DatasetVersion,
            Name = plan.Name,
            CreatedAt = now,
            CreatedByAdminId = access.AdministratorId,
        };
        var runs = plan.Configurations.Select(configuration => new EvaluationRun
        {
            Id = ids.NewGuid(),
            ComparisonId = comparison.Id,
            TenantId = plan.TenantId,
            DatasetId = plan.DatasetId,
            DatasetVersion = plan.DatasetVersion,
            RunConfigurationJson = configuration.ToJson(),
            ConfigSha256 = configuration.Sha256(),
            MaxEstimatedCost = plan.MaxEstimatedCost,
            Status = EvaluationRunStatuses.Queued,
            CasesTotal = plan.CasesTotal,
            CreatedAt = now,
        }).ToList();

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Holds the Dataset and Prompts against a concurrent delete (which locks them FOR UPDATE) until this Comparison is committed,
        // so a delete either sees the Comparison or happened first, and then the Comparison is refused here.
        var dataset = await db.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM evaluation.dataset WHERE id = {plan.DatasetId} FOR KEY SHARE")
            .ToListAsync(cancellationToken);
        if (dataset.Count == 0)
        {
            return new Error(ErrorCode.NotFound, "The dataset was not found.");
        }

        foreach (var promptId in plan.Configurations.Select(c => c.Prompt.Id).Distinct().Order())
        {
            var prompt = await db.Database
                .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM evaluation.prompt WHERE id = {promptId} FOR KEY SHARE")
                .ToListAsync(cancellationToken);
            if (prompt.Count == 0)
            {
                return new Error(ErrorCode.NotFound, "The prompt was not found.");
            }
        }

        db.Comparisons.Add(comparison);
        db.Runs.AddRange(runs);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var run in runs)
        {
            await jobs.EnqueueAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), EvaluationRunJob.Request(run.Id), cancellationToken);
        }

        var after = new Dictionary<string, object?>
        {
            ["name"] = comparison.Name,
            ["datasetId"] = comparison.DatasetId,
            ["datasetVersion"] = comparison.DatasetVersion,
            ["runIds"] = runs.Select(r => r.Id).ToArray(),
            ["configSha256"] = runs.Select(r => r.ConfigSha256).ToArray(),
            ["maxEstimatedCost"] = plan.MaxEstimatedCost,
        };
        if (rerunOf is { } source)
        {
            after["rerunOf"] = source;
        }

        var entry = new AuditEntry(EvaluationAuditActions.EvaluationCreated, "comparison", comparison.Id.ToString("D", CultureInfo.InvariantCulture))
        {
            TenantId = plan.TenantId,
            After = after,
        };
        await audit.WriteAsync(entry, transaction.GetDbTransaction(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedEvaluationView(comparison.Id, [.. runs.Select(r => r.Id)]);
    }

    private static Error AlreadyFinished(string status) => new(ErrorCode.Conflict, $"The run is already {status}.");

    private static Error ComparisonNotFound() => new(ErrorCode.NotFound, "The comparison was not found.");

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>A run row locked by <see cref="EvaluationRunService.DeleteComparisonAsync"/>.</summary>
internal sealed record LockedRun(Guid Id, string Status, bool HasCompletedAt);
