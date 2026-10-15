using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.SharedKernel.Http;

public static class HttpPipelineExtensions
{
    /// <summary>
    /// Adds <see cref="RequestIdMiddleware"/> and a last-resort exception handler, in that order, so even an unhandled
    /// failure answers with Problem Details and an <c>X-Request-Id</c>. Call before authentication.
    /// </summary>
    public static IApplicationBuilder UseRequestIdAndErrorHandling(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseMiddleware<RequestIdMiddleware>();
        app.UseExceptionHandler(handler => handler.Run(context =>
        {
            var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            return ProblemResults.From(context, Map(exception)).ExecuteAsync(context);
        }));
        return app;
    }

    // Exception text is never copied into the response: it could carry request content (ADR-0010).
    internal static Error Map(Exception? exception) => exception is BadHttpRequestException bad
        ? bad.StatusCode == StatusCodes.Status413PayloadTooLarge
            ? new Error(ErrorCode.PayloadTooLarge, "The request body is too large.")
            : new Error(ErrorCode.InvalidRequest, "The request could not be read.")
        : new Error(ErrorCode.InternalError, "An internal error occurred.");
}
