using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using ModelSwitch.Inference.Application;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Http;

namespace ModelSwitch.Inference.Endpoints;

/// <summary>
/// Step 2 of the request lifecycle (architecture.md section 5): rejects a wrong content type with 415 and an oversized
/// body with 413 before authentication does any work. A declared Content-Length is checked up front; for a chunked body
/// Kestrel's per-request limit makes the read fail, which the shared exception handler maps to 413. Transport only.
/// </summary>
internal sealed class InferenceRequestGuardMiddleware(RequestDelegate next, InferenceOptions options)
{
    private static readonly PathString _chatCompletionsPath = new("/v1/chat/completions");

    public Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || !context.Request.Path.Equals(_chatCompletionsPath, StringComparison.OrdinalIgnoreCase))
        {
            return next(context);
        }

        if (!IsJson(context.Request.ContentType))
        {
            return Reject(context, new Error(ErrorCode.UnsupportedMediaType, "The Content-Type must be application/json."));
        }

        if (context.Request.ContentLength > options.MaxRequestBodyBytes)
        {
            return Reject(context, new Error(ErrorCode.PayloadTooLarge, $"The request body must not exceed {options.MaxRequestBodyBytes} bytes."));
        }

        var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = options.MaxRequestBodyBytes;
        }

        return next(context);
    }

    private static Task Reject(HttpContext context, Error error) => ProblemResults.From(context, error).ExecuteAsync(context);

    private static bool IsJson(string? contentType)
        => contentType is not null
            && System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            && string.Equals(parsed.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);
}
