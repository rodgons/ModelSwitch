using System.Text.Json;
using ModelSwitch.Evaluation.Domain.Evaluators;
using ModelSwitch.Evaluation.Domain.Runs;
using ModelSwitch.Evaluation.Domain.Templates;
using ModelSwitch.Inference.Contracts;

namespace ModelSwitch.Evaluation.Application;

/// <summary>
/// One Case's worst-case Estimated Cost (USD): what the Budget Reservation of its candidate call would hold, plus, for
/// <c>llm_judge</c>, its judge call's (whose input quotes the candidate output, counted at the candidate's output ceiling).
/// <see cref="ErrorCode"/> is set when the Case could not be costed: its prompt does not render (no call will be made) or routing
/// refuses it (the call would fail the same way); such a Case counts as 0.
/// </summary>
internal sealed record CaseWorstCase(decimal Candidate, decimal Judge, string? ErrorCode)
{
    public decimal Total => Candidate + Judge;
}

/// <summary>Works out <see cref="CaseWorstCase"/> through <see cref="IInferenceService.EstimateWorstCaseAsync"/>; nothing is recorded.</summary>
internal sealed class CaseCosting(IInferenceService inference)
{
    public async Task<CaseWorstCase> EstimateAsync(
        Guid tenantId,
        Guid runId,
        RunConfiguration configuration,
        IEvaluator evaluator,
        RenderResult rendered,
        JsonElement input,
        JsonElement expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rendered);
        if (rendered.Prompt is not { } prompt)
        {
            return new CaseWorstCase(0m, 0m, rendered.Error!.Code);
        }

        // Nothing is recorded for an estimate, so the request id is never used.
        var candidate = await inference.EstimateWorstCaseAsync(RunInference.Candidate(Guid.Empty, tenantId, runId, configuration, prompt), 0, cancellationToken);
        if (candidate.IsFailure)
        {
            return new CaseWorstCase(0m, 0m, candidate.Error.Code.Value);
        }

        if (evaluator is not IJudgedEvaluator judged)
        {
            return new CaseWorstCase(candidate.Value.Amount.Amount, 0m, null);
        }

        var messages = judged.BuildJudgeMessages(new CaseContext(input, expected, string.Empty));
        var judge = await inference.EstimateWorstCaseAsync(
            RunInference.Judge(Guid.Empty, tenantId, runId, configuration, messages),
            candidate.Value.MaxOutputTokens,
            cancellationToken);
        return judge.IsFailure
            ? new CaseWorstCase(candidate.Value.Amount.Amount, 0m, judge.Error.Code.Value)
            : new CaseWorstCase(candidate.Value.Amount.Amount, judge.Value.Amount.Amount, null);
    }
}
