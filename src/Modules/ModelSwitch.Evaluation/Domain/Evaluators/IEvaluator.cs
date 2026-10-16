using ModelSwitch.Evaluation.Domain.Templates;

namespace ModelSwitch.Evaluation.Domain.Evaluators;

/// <summary>
/// A versioned scoring method (evaluation.md section 3). Every instance is exactly one of <see cref="ILocalEvaluator"/>,
/// which scores the candidate output by itself, or <see cref="IJudgedEvaluator"/>, which needs one judge Inference Request
/// per Case. The run executor branches on <see cref="RequiresJudge"/> (or the type).
/// </summary>
internal interface IEvaluator
{
    /// <summary>The kind, e.g. <c>json_fields</c>.</summary>
    string Kind { get; }

    /// <summary>The semantic version, bumped whenever the scoring changes; stored in the Run Configuration.</summary>
    string Version { get; }

    bool RequiresJudge { get; }
}

/// <summary>An Evaluator that scores without any model call.</summary>
internal interface ILocalEvaluator : IEvaluator
{
    /// <summary>Scores one successful candidate output. Never throws for bad output: that is a failed Case.</summary>
    CaseScore Score(CaseContext context);
}

/// <summary>
/// An Evaluator whose score comes from a judge model. The executor (1) sends <see cref="BuildJudgeMessages"/> to
/// <see cref="JudgeAlias"/> through <c>IInferenceService</c> as its own Inference Request (Request Origin <c>evaluation</c>),
/// then (2) passes the reply text to <see cref="ScoreJudgeReply"/>. If the judge call itself fails, the executor errors the
/// Case with the inference error code; this interface is not involved.
/// </summary>
internal interface IJudgedEvaluator : IEvaluator
{
    /// <summary>The Model Alias of the judge, pinned in the Evaluator config.</summary>
    string JudgeAlias { get; }

    IReadOnlyList<PromptMessage> BuildJudgeMessages(CaseContext context);

    /// <summary>The Case's score from the judge's reply; an unparseable reply errors the Case.</summary>
    CaseScore ScoreJudgeReply(CaseContext context, string judgeReply);
}
