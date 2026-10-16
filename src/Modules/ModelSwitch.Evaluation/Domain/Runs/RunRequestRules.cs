using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelSwitch.Evaluation.Domain.Evaluators;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Evaluation.Domain.Runs;

/// <summary>The body of <c>POST /admin/evaluations</c> (and of the cost estimate), as bound from JSON.</summary>
internal sealed record CreateEvaluationRequest(
    Guid? TenantId,
    Guid? DatasetId,
    int? DatasetVersion,
    string? Name,
    IReadOnlyList<RunConfigurationInput?>? Configurations,
    int? Concurrency,
    decimal? MaxEstimatedCost);

/// <summary>One Run Configuration of the request: target, Prompt Version, generation parameters and Evaluator.</summary>
internal sealed record RunConfigurationInput(TargetInput? Target, PromptRefInput? Prompt, GenerationParamsInput? Params, EvaluatorInput? Evaluator);

/// <summary>Exactly one of a Model Alias and a Provider Model id.</summary>
internal sealed record TargetInput(string? Alias, Guid? ProviderModelId);

internal sealed record PromptRefInput(Guid? Id, int? Version);

internal sealed record GenerationParamsInput(double? Temperature, double? TopP, long? Seed, int? MaxOutputTokens);

internal sealed record EvaluatorInput(string? Kind, string? Version, JsonElement? Config);

/// <summary>A configuration whose shape is valid; its references (alias, model, prompt) are not resolved yet.</summary>
internal sealed record ValidatedConfiguration(
    int Index,
    string? Alias,
    Guid? ProviderModelId,
    Guid PromptId,
    int PromptVersion,
    GenerationParams Params,
    IEvaluator Evaluator,
    JsonElement EvaluatorConfig)
{
    /// <summary>The <c>errors</c> key prefix of this configuration, e.g. <c>configurations[1].</c>.</summary>
    public string Path => RunRequestRules.ConfigurationPath(Index);
}

internal sealed record ValidatedRunRequest(
    Guid TenantId,
    Guid DatasetId,
    int DatasetVersion,
    string Name,
    IReadOnlyList<ValidatedConfiguration> Configurations,
    int Concurrency,
    decimal? MaxEstimatedCost);

/// <summary>
/// The shape rules of an Evaluation request (evaluation.md section 4), checked all at once so one 400 <c>validation_failed</c>
/// reports every problem. Keys follow the body: <c>tenantId</c>, <c>configurations[0].target.alias</c>,
/// <c>configurations[1].evaluator.config.labels[0]</c> (the Evaluator registry's relative keys under the configuration's
/// <c>evaluator.</c>). References are resolved afterwards by the application service.
/// </summary>
internal static partial class RunRequestRules
{
    public const int MaxConfigurations = 6;
    public const int DefaultConcurrency = 4;
    public const int MaxConcurrency = 16;
    public const decimal MaxCost = 9_999_999_999m;

    public static string ConfigurationPath(int index) => $"configurations[{index.ToString(CultureInfo.InvariantCulture)}].";

    public static Result<ValidatedRunRequest> Validate(CreateEvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.TenantId is null || request.TenantId == Guid.Empty)
        {
            errors["tenantId"] = ["The tenantId is required."];
        }

        if (request.DatasetId is null || request.DatasetId == Guid.Empty)
        {
            errors["datasetId"] = ["The datasetId is required."];
        }

        if (request.DatasetVersion is not >= 1)
        {
            errors["datasetVersion"] = ["The datasetVersion is required: a Dataset Version number from 1."];
        }

        var name = ResourceNames.Validate(request.Name, out var nameError);
        if (nameError is not null)
        {
            errors["name"] = [nameError];
        }

        var concurrency = request.Concurrency ?? DefaultConcurrency;
        if (concurrency is < 1 or > MaxConcurrency)
        {
            errors["concurrency"] = [$"The concurrency must be from 1 to {MaxConcurrency}."];
        }

        if (request.MaxEstimatedCost is { } cap && (cap < 0m || cap > MaxCost || decimal.Round(cap, SharedKernel.Money.Scale) != cap))
        {
            errors["maxEstimatedCost"] = ["The maxEstimatedCost must be a USD amount from 0 to 9 999 999 999 with at most 10 decimals."];
        }

        var configurations = new List<ValidatedConfiguration>();
        if (request.Configurations is not { Count: >= 1 and <= MaxConfigurations } inputs)
        {
            errors["configurations"] = [$"Give 1 to {MaxConfigurations} Run Configurations."];
        }
        else
        {
            for (var i = 0; i < inputs.Count; i++)
            {
                if (ValidateConfiguration(i, inputs[i], errors) is { } valid)
                {
                    configurations.Add(valid);
                }
            }
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The evaluation is invalid.", errors);
        }

        return new ValidatedRunRequest(request.TenantId!.Value, request.DatasetId!.Value, request.DatasetVersion!.Value, name!, configurations, concurrency, request.MaxEstimatedCost);
    }

    private static ValidatedConfiguration? ValidateConfiguration(int index, RunConfigurationInput? input, Dictionary<string, string[]> errors)
    {
        var path = ConfigurationPath(index);
        if (input is null)
        {
            errors[path.TrimEnd('.')] = ["The Run Configuration is required."];
            return null;
        }

        var before = errors.Count;
        string? alias = null;
        Guid? providerModelId = null;
        switch (input.Target)
        {
            case null:
                errors[$"{path}target"] = ["The target is required: {alias} or {providerModelId}."];
                break;
            case { Alias: not null, ProviderModelId: not null } or { Alias: null, ProviderModelId: null }:
                errors[$"{path}target"] = ["The target names exactly one of alias and providerModelId."];
                break;
            case { Alias: { } name }:
                if (!AliasPattern().IsMatch(name))
                {
                    errors[$"{path}target.alias"] = ["The alias must match [a-z0-9][a-z0-9._-]{0,63}."];
                }

                alias = name;
                break;
            case { ProviderModelId: { } id }:
                if (id == Guid.Empty)
                {
                    errors[$"{path}target.providerModelId"] = ["The providerModelId must be a Provider Model id."];
                }

                providerModelId = id;
                break;
        }

        if (input.Prompt is null)
        {
            errors[$"{path}prompt"] = ["The prompt is required: {id, version}."];
        }
        else
        {
            if (input.Prompt.Id is null || input.Prompt.Id == Guid.Empty)
            {
                errors[$"{path}prompt.id"] = ["The prompt id is required."];
            }

            if (input.Prompt.Version is not >= 1)
            {
                errors[$"{path}prompt.version"] = ["The prompt version is required: a Prompt Version number from 1."];
            }
        }

        var parameters = input.Params ?? new GenerationParamsInput(null, null, null, null);
        if (parameters.Temperature is { } temperature && (double.IsNaN(temperature) || temperature is < 0 or > 2))
        {
            errors[$"{path}params.temperature"] = ["The temperature must be from 0 to 2."];
        }

        if (parameters.TopP is { } topP && (double.IsNaN(topP) || topP is < 0 or > 1))
        {
            errors[$"{path}params.topP"] = ["The topP must be from 0 to 1."];
        }

        if (parameters.MaxOutputTokens is < 1)
        {
            errors[$"{path}params.maxOutputTokens"] = ["The maxOutputTokens must be at least 1."];
        }

        IEvaluator? evaluator = null;
        var config = default(JsonElement);
        if (input.Evaluator is null)
        {
            errors[$"{path}evaluator"] = ["The evaluator is required: {kind, version, config}."];
        }
        else
        {
            config = input.Evaluator.Config is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } given
                ? given.Clone()
                : EvaluationJson.ToElement(new { });
            var created = EvaluatorRegistry.Create(input.Evaluator.Kind!, input.Evaluator.Version!, config);
            if (created.IsFailure)
            {
                foreach (var (key, messages) in created.Error.FieldErrors ?? new Dictionary<string, string[]>())
                {
                    errors[$"{path}evaluator.{key}"] = messages;
                }
            }
            else
            {
                evaluator = created.Value;
            }
        }

        if (errors.Count > before || evaluator is null)
        {
            return null;
        }

        return new ValidatedConfiguration(
            index,
            alias,
            providerModelId,
            input.Prompt!.Id!.Value,
            input.Prompt.Version!.Value,
            new GenerationParams(parameters.Temperature, parameters.TopP, parameters.Seed, parameters.MaxOutputTokens),
            evaluator,
            config);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex AliasPattern();
}
