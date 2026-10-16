using System.Text.Json;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Evaluation.Domain.Runs;
using ModelSwitch.Evaluation.Domain.Templates;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Evaluation.Application;

/// <summary>
/// Builds the Inference Requests of an Evaluation Run (evaluation.md section 5): Request Origin <c>evaluation</c>, the run's id, no
/// API Key, <c>TaskType</c> = the Dataset's task kind, the frozen target, and for the candidate call the Prompt Version's messages,
/// its <c>responseFormat</c> and the generation parameters.
/// </summary>
internal static class RunInference
{
    /// <summary>The name a <c>json_schema</c> response format is sent under when the Prompt Version gives none.</summary>
    public const string DefaultSchemaName = "response";

    /// <summary>The frozen target: an alias on its frozen Policy Version, or the pinned Provider Model.</summary>
    public static InferenceTarget Target(FrozenTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target switch
        {
            { Alias: { } alias, PolicyId: { } policyId, PolicyVersion: { } version } => InferenceTarget.For(alias, policyId, version),
            { ProviderModelId: { } providerModelId } => InferenceTarget.Pinned(providerModelId),
            _ => throw new InvalidOperationException("The frozen target names neither an alias with its Policy Version nor a Provider Model."),
        };
    }

    public static InferenceRequest Candidate(Guid requestId, Guid tenantId, Guid runId, RunConfiguration configuration, RenderedPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(prompt);
        var chat = new NeutralChatRequest(Messages(prompt.Messages))
        {
            Temperature = configuration.Params.Temperature,
            TopP = configuration.Params.TopP,
            Seed = configuration.Params.Seed,
            MaxOutputTokens = configuration.Params.MaxOutputTokens,
            ResponseFormat = ResponseFormatOf(prompt.ResponseFormat),
        };
        return Evaluation(new InferenceRequest(requestId, tenantId, Target(configuration.Target), chat), runId, configuration.Dataset.TaskKind);
    }

    /// <summary>The <c>llm_judge</c> call: the Evaluator's messages to the frozen judge alias, with no generation parameters of its own.</summary>
    public static InferenceRequest Judge(Guid requestId, Guid tenantId, Guid runId, RunConfiguration configuration, IReadOnlyList<PromptMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var judge = configuration.Evaluator.Judge ?? throw new InvalidOperationException("The run's llm_judge Evaluator has no frozen judge alias.");
        var target = InferenceTarget.For(judge.Alias, judge.PolicyId, judge.PolicyVersion);
        return Evaluation(new InferenceRequest(requestId, tenantId, target, new NeutralChatRequest(Messages(messages))), runId, configuration.Dataset.TaskKind);
    }

    /// <summary>
    /// The Prompt Version's <c>responseFormat</c> (validated when the version was saved): <c>text</c>, <c>json_object</c>, or
    /// <c>json_schema</c> with its schema, its <c>name</c> (default <see cref="DefaultSchemaName"/>) and <c>strict</c> (default false).
    /// </summary>
    public static ResponseFormat ResponseFormatOf(JsonElement? format)
    {
        if (format is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty("type", out var type))
        {
            return new ResponseFormat.Text();
        }

        return type.GetString() switch
        {
            "json_object" => new ResponseFormat.JsonObject(),
            "json_schema" => new ResponseFormat.JsonSchema(
                value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : DefaultSchemaName,
                value.GetProperty("schema").Clone(),
                value.TryGetProperty("strict", out var strict) && strict.ValueKind == JsonValueKind.True),
            _ => new ResponseFormat.Text(),
        };
    }

    private static InferenceRequest Evaluation(InferenceRequest request, Guid runId, string taskKind)
        => request with { Origin = RequestOrigin.Evaluation, EvaluationRunId = runId, TaskType = taskKind };

    private static NeutralMessage[] Messages(IReadOnlyList<PromptMessage> messages)
        => [.. messages.Select(m => new NeutralMessage(
            m.Role switch
            {
                "system" => MessageRole.System,
                "assistant" => MessageRole.Assistant,
                _ => MessageRole.User,
            },
            m.Content))];
}
