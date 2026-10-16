using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelSwitch.Evaluation.Domain.Datasets;
using ModelSwitch.Evaluation.Domain.Evaluators;
using ModelSwitch.Evaluation.Domain.Runs;
using ModelSwitch.Evaluation.Infrastructure;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Routing.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Evaluation.Application;

/// <summary>A validated request with every reference resolved and each Run Configuration frozen, ready to be created or costed.</summary>
internal sealed record PlannedRun(
    Guid TenantId,
    Guid DatasetId,
    int DatasetVersion,
    string Name,
    int CasesTotal,
    IReadOnlyList<RunConfiguration> Configurations,
    decimal? MaxEstimatedCost);

/// <summary>
/// Resolves and freezes an Evaluation request (evaluation.md section 4). The order hides other Tenants: a <c>tenantId</c> outside the
/// caller's grant (or unknown) is 404 before anything else is looked at. Then every shape problem and every unresolved reference is
/// reported together as 400 <c>validation_failed</c>: a Dataset or Prompt that is not the Tenant's, an unknown version, an unknown
/// alias, a disabled Provider Model, an Evaluator that does not fit the Dataset Version. An alias freezes the Policy Version current
/// now (<see cref="IPolicyStore.GetActiveForAliasAsync"/>), so later policy edits never change the run; the <c>llm_judge</c> alias is
/// frozen the same way.
/// </summary>
internal sealed class RunPlanner(
    IDbContextFactory<EvaluationDbContext> contexts,
    EvaluationAccess access,
    ITenantDirectory tenants,
    IPolicyStore policies,
    IProviderCatalog catalog)
{
    private static readonly Error _notFound = new(ErrorCode.NotFound, "The resource was not found.");

    public async Task<Result<PlannedRun>> PlanAsync(CreateEvaluationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TenantId is { } tenantId && tenantId != Guid.Empty)
        {
            if (access.CheckTenant(tenantId).IsFailure || !await tenants.ExistsAsync(tenantId, cancellationToken))
            {
                return _notFound;
            }
        }

        var validated = RunRequestRules.Validate(request);
        if (validated.IsFailure)
        {
            return validated.Error;
        }

        var run = validated.Value;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var dataset = await db.Datasets.AsNoTracking().FirstOrDefaultAsync(d => d.Id == run.DatasetId && d.TenantId == run.TenantId, cancellationToken);
        var version = dataset is null
            ? null
            : await db.DatasetVersions.AsNoTracking().FirstOrDefaultAsync(v => v.DatasetId == run.DatasetId && v.Version == run.DatasetVersion, cancellationToken);
        if (dataset is null)
        {
            errors["datasetId"] = ["The Tenant has no such dataset."];
        }
        else if (version is null)
        {
            errors["datasetVersion"] = ["The dataset has no such version."];
        }

        var frozen = new List<RunConfiguration>();
        foreach (var configuration in run.Configurations)
        {
            if (await FreezeAsync(db, run, configuration, dataset, version, errors, cancellationToken) is { } resolved)
            {
                frozen.Add(resolved);
            }
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The evaluation is invalid.", errors);
        }

        return new PlannedRun(run.TenantId, run.DatasetId, run.DatasetVersion, run.Name, version!.CaseCount, frozen, run.MaxEstimatedCost);
    }

    private async Task<RunConfiguration?> FreezeAsync(
        EvaluationDbContext db,
        ValidatedRunRequest run,
        ValidatedConfiguration configuration,
        Dataset? dataset,
        DatasetVersion? version,
        Dictionary<string, string[]> errors,
        CancellationToken cancellationToken)
    {
        var path = configuration.Path;
        var before = errors.Count;

        var prompt = await db.Prompts.AsNoTracking().AnyAsync(p => p.Id == configuration.PromptId && p.TenantId == run.TenantId, cancellationToken);
        var promptVersion = prompt
            ? await db.PromptVersions.AsNoTracking().FirstOrDefaultAsync(v => v.PromptId == configuration.PromptId && v.Version == configuration.PromptVersion, cancellationToken)
            : null;
        if (!prompt)
        {
            errors[$"{path}prompt.id"] = ["The Tenant has no such prompt."];
        }
        else if (promptVersion is null)
        {
            errors[$"{path}prompt.version"] = ["The prompt has no such version."];
        }

        FrozenTarget? target = null;
        if (configuration.Alias is { } alias)
        {
            var policy = await policies.GetActiveForAliasAsync(alias, cancellationToken);
            if (policy is null)
            {
                errors[$"{path}target.alias"] = ["No Model Alias has this name."];
            }
            else
            {
                target = FrozenTarget.ForAlias(alias, policy.PolicyId, policy.Version);
            }
        }
        else if (configuration.ProviderModelId is { } providerModelId)
        {
            var model = await catalog.FindEnabledByIdAsync(providerModelId, cancellationToken);
            if (model is null)
            {
                errors[$"{path}target.providerModelId"] = ["No enabled Provider Model has this id."];
            }
            else
            {
                target = FrozenTarget.ForProviderModel(providerModelId, $"{model.ProviderName}/{model.ProviderModelIdentifier}");
            }
        }

        FrozenJudge? judge = null;
        if (configuration.Evaluator is IJudgedEvaluator judged)
        {
            var policy = await policies.GetActiveForAliasAsync(judged.JudgeAlias, cancellationToken);
            if (policy is null)
            {
                errors[$"{path}evaluator.config.judgeAlias"] = ["No Model Alias has this name."];
            }
            else
            {
                judge = new FrozenJudge(judged.JudgeAlias, policy.PolicyId, policy.Version);
            }
        }

        if (dataset is not null && version is not null && TaskKinds.TryParse(dataset.TaskKind, out var task))
        {
            using var schema = JsonDocument.Parse(version.ScoringSchemaJson);
            foreach (var (key, messages) in EvaluatorFit.Check(configuration.Evaluator, task, schema.RootElement, $"{path}evaluator."))
            {
                errors[key] = messages;
            }
        }

        if (errors.Count > before || dataset is null || version is null || promptVersion is null || target is null)
        {
            return null;
        }

        using var template = JsonDocument.Parse(promptVersion.TemplateJson);
        return new RunConfiguration(
            new FrozenDataset(dataset.Id, version.Version, version.ContentSha256, dataset.TaskKind),
            target,
            new FrozenPrompt(promptVersion.PromptId, promptVersion.Version, template.RootElement.Clone()),
            configuration.Params,
            new FrozenEvaluator(configuration.Evaluator.Kind, configuration.Evaluator.Version, configuration.EvaluatorConfig) { Judge = judge },
            run.Concurrency,
            GatewayBuild.Version);
    }
}
