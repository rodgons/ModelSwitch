using System.Text;
using System.Text.Json;
using ModelSwitch.Evaluation.Domain.Templates;
using ModelSwitch.SharedKernel;

namespace ModelSwitch.Evaluation.Domain.Evaluators;

/// <summary>
/// <c>llm_judge</c> v1, for free text: a judge model scores the output against a rubric. Results are model-judged and labelled
/// so in the UI.
/// </summary>
/// <remarks>
/// <para>Config: <c>{"judgeAlias": string, "rubric": string, "passThreshold": 0-1 (default 0.5)}</c>, both strings required.</para>
/// <para>The judge gets a system message fixing the reply format and a user message with the rubric, the Case input (compact
/// JSON), the expected result when the Case has one, and the candidate output, each in its own tagged block.</para>
/// <para>Reply format (strict): exactly one JSON object <c>{"score": number from 0 to 1, "reason": string (optional)}</c>; a
/// Markdown fence around it is tolerated, nothing else is. Any other reply errors the Case with <c>provider_error</c> (the
/// judge, a Provider, failed to answer usably): there is no score, so it counts toward the errored rate, not quality.</para>
/// <para>Passed means score ≥ <c>passThreshold</c>. Details: <c>{"modelJudged": true, "judgeAlias", "reason"}</c>.</para>
/// </remarks>
internal sealed class LlmJudgeEvaluator(string judgeAlias, string rubric, decimal passThreshold) : IJudgedEvaluator
{
    public const string KindName = "llm_judge";

    public const decimal DefaultPassThreshold = 0.5m;

    internal const string SystemPrompt =
        "You are an evaluation judge. Score the candidate output against the rubric, using the case input and the expected "
        + "result (when given) as context. Reply with only a JSON object of the form "
        + "{\"score\": <number from 0 to 1>, \"reason\": \"<one short sentence>\"} and nothing else.";

    public string Kind => KindName;

    public string Version => "1";

    public bool RequiresJudge => true;

    public string JudgeAlias { get; } = judgeAlias;

    public string Rubric { get; } = rubric;

    public decimal PassThreshold { get; } = passThreshold;

    public static Result<IEvaluator> Create(JsonConfigReader config)
    {
        var judgeAlias = config.String("judgeAlias", required: true);
        var rubric = config.String("rubric", required: true);
        var passThreshold = config.PassThreshold(DefaultPassThreshold);
        return config.Finish<IEvaluator>(() => new LlmJudgeEvaluator(judgeAlias!, rubric!, passThreshold), "The llm_judge config is invalid.");
    }

    public IReadOnlyList<PromptMessage> BuildJudgeMessages(CaseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var user = new StringBuilder()
            .Append("Rubric:\n").Append(Rubric).Append("\n\n")
            .Append("Case input:\n<case_input>\n").Append(EvaluationJson.Text(context.Input)).Append("\n</case_input>\n\n");
        if (context.Expected.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            user.Append("Expected result:\n<expected_result>\n").Append(EvaluationJson.Text(context.Expected)).Append("\n</expected_result>\n\n");
        }

        user.Append("Candidate output:\n<candidate_output>\n").Append(context.Output).Append("\n</candidate_output>");
        return [new PromptMessage("system", SystemPrompt), new PromptMessage("user", user.ToString())];
    }

    public CaseScore ScoreJudgeReply(CaseContext context, string judgeReply)
    {
        ArgumentNullException.ThrowIfNull(judgeReply);
        if (EvaluationJson.ParseObject(judgeReply) is { } reply
            && reply.TryGetProperty("score", out var scoreElement)
            && scoreElement.ValueKind == JsonValueKind.Number
            && scoreElement.TryGetDecimal(out var score)
            && score is >= 0m and <= 1m
            && (!reply.TryGetProperty("reason", out var reason) || reason.ValueKind is JsonValueKind.String or JsonValueKind.Null))
        {
            var rounded = decimal.Round(score, CaseScore.ScoreScale, MidpointRounding.ToEven);
            var reasonText = reply.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            return CaseScore.Scored(score, rounded >= PassThreshold, null, new { ModelJudged = true, JudgeAlias, Reason = reasonText });
        }

        return CaseScore.Errored(
            new CaseError(CaseErrorCodes.JudgeReplyUnparseable, "The judge's reply was not a JSON object with a score from 0 to 1."),
            new { ModelJudged = true, JudgeAlias, Error = "The judge's reply was not a JSON object with a score from 0 to 1." });
    }
}
