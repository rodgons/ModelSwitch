using Microsoft.AspNetCore.Http;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.SharedKernel.Http;

/// <summary>
/// The one writer that turns an <see cref="Error"/> into an RFC 9457 Problem Details response (ADR-0012). Status and
/// <c>error.type</c> come from the <see cref="ErrorCode"/>. Under <c>/v1</c> the body also carries the OpenAI
/// <c>error</c> member so OpenAI SDKs can still read it. Messages are client-safe by contract (ADR-0010).
/// </summary>
public static class ProblemResults
{
    private const string _inferencePrefix = "/v1";

    public static IResult From(HttpContext context, Error error)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(error);

        var status = error.Code.HttpStatus ?? StatusCodes.Status500InternalServerError;
        var requestId = RequestId.Get(context);
        var requestIdText = requestId == Guid.Empty ? null : requestId.ToString();

        var extensions = new Dictionary<string, object?>
        {
            ["errorCode"] = error.Code.Value,
            ["requestId"] = requestIdText,
        };

        if (error.FieldErrors is { Count: > 0 })
        {
            extensions["errors"] = error.FieldErrors;
        }

        if (context.Request.Path.StartsWithSegments(_inferencePrefix, StringComparison.Ordinal))
        {
            extensions["error"] = new Dictionary<string, object?>
            {
                ["message"] = error.Message,
                ["type"] = error.Code.Type,
                ["code"] = error.Code.Value,
                ["param"] = error.FieldErrors?.Keys.FirstOrDefault(),
                ["request_id"] = requestIdText,
            };
        }

        return Results.Problem(
            statusCode: status,
            title: error.Code.Title,
            detail: error.Message,
            type: $"/errors/{error.Code.Value}",
            instance: context.Request.Path,
            extensions: extensions);
    }
}
