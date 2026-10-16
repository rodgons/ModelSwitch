using System.Text.Json;
using ModelSwitch.Evaluation.Domain.Metrics;
using ModelSwitch.SharedKernel;

namespace ModelSwitch.Evaluation.Domain.Evaluators;

/// <summary>
/// <c>reference_similarity</c> v1, for free text. The score is the SQuAD token F1 (<see cref="TokenF1"/>) of the output against
/// the reference; when the expected result is an array of reference strings, the best F1 over them (as SQuAD scores several
/// gold answers). Passed means score ≥ <c>passThreshold</c> (default 0.5). <c>predicted</c> is null. Config:
/// <c>{"passThreshold": 0-1}</c>. Details: <c>{"f1": number}</c>.
/// </summary>
internal sealed class ReferenceSimilarityEvaluator(decimal passThreshold) : ILocalEvaluator
{
    public const string KindName = "reference_similarity";

    public const decimal DefaultPassThreshold = 0.5m;

    public string Kind => KindName;

    public string Version => "1";

    public bool RequiresJudge => false;

    public decimal PassThreshold { get; } = passThreshold;

    public static Result<IEvaluator> Create(JsonConfigReader config)
    {
        var passThreshold = config.PassThreshold(DefaultPassThreshold);
        return config.Finish<IEvaluator>(() => new ReferenceSimilarityEvaluator(passThreshold), "The reference_similarity config is invalid.");
    }

    public CaseScore Score(CaseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var references = References(context.Expected);
        if (references is null)
        {
            return CaseScore.Errored(new CaseError(
                CaseErrorCodes.ExpectedResultInvalid,
                "reference_similarity needs a reference string, or a non-empty array of them, as the expected result."));
        }

        var f1 = references.Max(reference => TokenF1.Score(context.Output, reference));
        var rounded = decimal.Round(f1, CaseScore.ScoreScale, MidpointRounding.ToEven);
        return CaseScore.Scored(f1, rounded >= PassThreshold, null, new { F1 = rounded });
    }

    private static string[]? References(JsonElement expected) => expected.ValueKind switch
    {
        JsonValueKind.String => [expected.GetString()!],
        JsonValueKind.Array when expected.GetArrayLength() > 0 && expected.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String)
            => expected.EnumerateArray().Select(e => e.GetString()!).ToArray(),
        _ => null,
    };
}
