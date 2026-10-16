namespace ModelSwitch.Evaluation.Contracts;

/// <summary>
/// The Case Result an evaluation Inference Request was made for: "candidate for Case X of run Y" or "judge for Case X of run Y"
/// (evaluation.md section 3). Metadata only: no Case content.
/// </summary>
/// <param name="RunId">The Evaluation Run.</param>
/// <param name="CaseId">The gateway's Case id.</param>
/// <param name="ExternalId">The Case's JSONL <c>id</c>.</param>
/// <param name="Role"><c>candidate</c> or <c>judge</c>.</param>
public sealed record EvaluationCaseLink(Guid RunId, Guid CaseId, string ExternalId, string Role)
{
    public const string Candidate = "candidate";
    public const string Judge = "judge";
}

/// <summary>Follows an Inference Request back to its Case Result, for the request detail page (Admin module).</summary>
public interface IEvaluationCaseLinks
{
    /// <summary>
    /// The Case Result whose candidate or judge call was <paramref name="requestId"/>, or null when none links to it. The caller has
    /// already checked the request's Tenant against the Tenant Grant; a Case Result always belongs to the same Tenant as its requests.
    /// </summary>
    Task<EvaluationCaseLink?> FindByRequestAsync(Guid requestId, CancellationToken cancellationToken);
}
