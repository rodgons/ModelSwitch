using System.Text.Json;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Evaluation.Domain.Evaluators;

/// <summary>
/// Builds an Evaluator from a Run Configuration's <c>evaluator</c> object (kind, version, config). Validation errors are
/// keyed relative to that object (<c>kind</c>, <c>version</c>, <c>config.labels[1]</c>), so a caller prefixes its own path,
/// e.g. <c>configurations[0].evaluator.</c>, before returning 400 <c>validation_failed</c>.
/// </summary>
internal static class EvaluatorRegistry
{
    private static readonly Dictionary<string, Dictionary<string, Func<JsonConfigReader, Result<IEvaluator>>>> _factories =
        new(StringComparer.Ordinal)
        {
            [ExactMatchEvaluator.KindName] = new(StringComparer.Ordinal) { ["1"] = ExactMatchEvaluator.Create },
            [JsonFieldsEvaluator.KindName] = new(StringComparer.Ordinal) { ["1"] = JsonFieldsEvaluator.Create },
            [ClassificationEvaluator.KindName] = new(StringComparer.Ordinal) { ["1"] = ClassificationEvaluator.Create },
            [ReferenceSimilarityEvaluator.KindName] = new(StringComparer.Ordinal) { ["1"] = ReferenceSimilarityEvaluator.Create },
            [LlmJudgeEvaluator.KindName] = new(StringComparer.Ordinal) { ["1"] = LlmJudgeEvaluator.Create },
        };

    public static IReadOnlyCollection<string> Kinds => _factories.Keys;

    /// <param name="kind">e.g. <c>json_fields</c>.</param>
    /// <param name="version">e.g. <c>"1"</c>.</param>
    /// <param name="config">The kind's config object; null (or JSON null) means <c>{}</c>.</param>
    public static Result<IEvaluator> Create(string kind, string version, JsonElement? config)
    {
        if (kind is null || !_factories.TryGetValue(kind, out var versions))
        {
            return Invalid("kind", $"Must be one of: {string.Join(", ", _factories.Keys.Order(StringComparer.Ordinal))}.");
        }

        if (version is null || !versions.TryGetValue(version, out var factory))
        {
            return Invalid("version", $"Must be one of: {string.Join(", ", versions.Keys)} for {kind}.");
        }

        return factory(new JsonConfigReader(config, "config"));
    }

    private static Error Invalid(string path, string message)
        => Error.Validation("The evaluator is invalid.", new Dictionary<string, string[]>(StringComparer.Ordinal) { [path] = [message] });
}
