using System.Text.Json;
using ModelSwitch.Evaluation.Domain.Evaluators;

namespace ModelSwitch.UnitTests.Evaluation;

internal static class EvaluatorTestSupport
{
    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static IEvaluator Create(string kind, string config = "{}")
    {
        var result = EvaluatorRegistry.Create(kind, "1", Json(config));
        if (result.IsFailure)
        {
            throw new InvalidOperationException(string.Join("; ", result.Error.FieldErrors!.Select(e => $"{e.Key}: {string.Join(",", e.Value)}")));
        }

        return result.Value;
    }

    public static CaseScore Score(string kind, string config, string expectedJson, string output, string inputJson = "{}")
        => ((ILocalEvaluator)Create(kind, config)).Score(new CaseContext(Json(inputJson), Json(expectedJson), output));
}
