using System.Text.Json;
using ModelSwitch.Evaluation.Domain.Datasets;
using ModelSwitch.Evaluation.Domain.Evaluators;

namespace ModelSwitch.Evaluation.Domain.Runs;

/// <summary>
/// Whether an Evaluator can score a Dataset Version (evaluation.md section 3, the Task column), checked at run creation:
/// <list type="bullet">
/// <item><c>exact_match</c> fits any task; <c>json_fields</c> extraction; <c>classification</c> classification; <c>llm_judge</c> and
/// <c>reference_similarity</c> free text. Otherwise <c>&lt;prefix&gt;kind</c>.</item>
/// <item><c>classification</c>: its labels must include every label the version expects (its <c>scoringSchema.labels</c>), else an
/// expected label could never be predicted (<c>&lt;prefix&gt;config.labels</c>).</item>
/// <item><c>json_fields</c>: a configured field must be one the version expects (<c>&lt;prefix&gt;config.fields.&lt;name&gt;</c>).</item>
/// </list>
/// Messages name counts and field names from the config, never Case content.
/// </summary>
internal static class EvaluatorFit
{
    public static Dictionary<string, string[]> Check(IEvaluator evaluator, TaskKind task, JsonElement scoringSchema, string prefix)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var fits = evaluator.Kind switch
        {
            ExactMatchEvaluator.KindName => true,
            JsonFieldsEvaluator.KindName => task == TaskKind.Extraction,
            ClassificationEvaluator.KindName => task == TaskKind.Classification,
            LlmJudgeEvaluator.KindName or ReferenceSimilarityEvaluator.KindName => task == TaskKind.FreeText,
            _ => false,
        };
        if (!fits)
        {
            errors[$"{prefix}kind"] = [$"The {evaluator.Kind} evaluator cannot score a {TaskKinds.ToWire(task)} dataset."];
            return errors;
        }

        if (evaluator is ClassificationEvaluator classification)
        {
            var configured = new HashSet<string>(classification.Labels, StringComparer.Ordinal);
            var missing = Strings(scoringSchema, "labels").Count(label => !configured.Contains(label));
            if (missing > 0)
            {
                errors[$"{prefix}config.labels"] = [$"The labels must include every label the Dataset Version expects ({missing} missing)."];
            }
        }
        else if (evaluator is JsonFieldsEvaluator jsonFields)
        {
            var expected = new HashSet<string>(Strings(scoringSchema, "fields"), StringComparer.Ordinal);
            foreach (var field in jsonFields.ConfiguredFields.Where(f => !expected.Contains(f)))
            {
                errors[$"{prefix}config.fields.{field}"] = ["The Dataset Version's expected results have no such field."];
            }
        }

        return errors;
    }

    private static IEnumerable<string> Strings(JsonElement schema, string property)
        => schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
            : [];
}
