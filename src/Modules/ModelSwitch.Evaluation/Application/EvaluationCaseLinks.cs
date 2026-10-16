using Microsoft.EntityFrameworkCore;
using ModelSwitch.Evaluation.Contracts;
using ModelSwitch.Evaluation.Infrastructure;

namespace ModelSwitch.Evaluation.Application;

/// <summary><see cref="IEvaluationCaseLinks"/> over <c>case_result</c>'s indexed <c>inference_request_id</c> and <c>judge_inference_request_id</c>.</summary>
internal sealed class EvaluationCaseLinks(IDbContextFactory<EvaluationDbContext> contexts) : IEvaluationCaseLinks
{
    public async Task<EvaluationCaseLink?> FindByRequestAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var link = await (
                from result in db.CaseResults.AsNoTracking()
                join @case in db.Cases.AsNoTracking() on result.CaseId equals @case.Id
                where result.InferenceRequestId == requestId || result.JudgeInferenceRequestId == requestId
                select new { result.RunId, result.CaseId, @case.ExternalId, IsJudge = result.JudgeInferenceRequestId == requestId })
            .FirstOrDefaultAsync(cancellationToken);
        return link is null
            ? null
            : new EvaluationCaseLink(link.RunId, link.CaseId, link.ExternalId, link.IsJudge ? EvaluationCaseLink.Judge : EvaluationCaseLink.Candidate);
    }
}
