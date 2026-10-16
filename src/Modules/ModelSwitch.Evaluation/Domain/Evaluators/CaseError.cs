using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Evaluation.Domain.Evaluators;

/// <summary>
/// Why one Case could not be scored: it becomes an errored Case Result whose <c>error_code</c> is <see cref="Code"/>.
/// <see cref="Message"/> names variables or fields, never Case content.
/// </summary>
internal sealed record CaseError(string Code, string Message);

/// <summary>The <c>case_result.error_code</c> values the Evaluation domain itself produces.</summary>
internal static class CaseErrorCodes
{
    /// <summary>A Prompt Version variable is absent from the Case input (evaluation.md section 2). A Case Result code, never an HTTP response.</summary>
    public static readonly string TemplateVariableMissing = ErrorCode.TemplateVariableMissing.Value;

    /// <summary>The judge model's reply did not follow the required format, so there is no score (the judge, a Provider, failed).</summary>
    public static readonly string JudgeReplyUnparseable = ErrorCode.ProviderError.Value;

    /// <summary>The Case's expected result has the wrong shape for the Evaluator (for example not an object for <c>json_fields</c>).</summary>
    public static readonly string ExpectedResultInvalid = ErrorCode.InvalidRequest.Value;
}
