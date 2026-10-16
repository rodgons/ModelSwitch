using ModelSwitch.SharedKernel;

namespace ModelSwitch.Evaluation.Domain.Evaluators;

/// <summary>
/// <c>exact_match</c> v1. Score 1 when the trimmed output equals the trimmed expected result, else 0; passed means score 1.
/// A string expected result is compared as its text, any other as compact JSON. <c>caseFold</c> (default false) compares
/// ignoring case. <c>predicted</c> is the trimmed output. Config: <c>{"caseFold": bool?}</c>.
/// </summary>
internal sealed class ExactMatchEvaluator(bool caseFold) : ILocalEvaluator
{
    public const string KindName = "exact_match";

    public string Kind => KindName;

    public string Version => "1";

    public bool RequiresJudge => false;

    public bool CaseFold { get; } = caseFold;

    public static Result<IEvaluator> Create(JsonConfigReader config)
    {
        var caseFold = config.Boolean("caseFold") ?? false;
        return config.Finish<IEvaluator>(() => new ExactMatchEvaluator(caseFold), "The exact_match config is invalid.");
    }

    public CaseScore Score(CaseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var output = context.Output.Trim();
        var expected = EvaluationJson.Text(context.Expected).Trim();
        var match = string.Equals(output, expected, CaseFold ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return CaseScore.Scored(match ? 1m : 0m, match, EvaluationJson.String(output), new { Match = match });
    }
}
