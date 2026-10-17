using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.SharedKernel.Http;

public static class HttpPipelineExtensions
{
    /// <summary>
    /// Makes a body or parameter that cannot be bound (malformed JSON, a value of the wrong type) throw, so the handler added by
    /// <see cref="UseRequestIdAndErrorHandling"/> answers 400 with Problem Details. ASP.NET Core throws only in Development by
    /// default; elsewhere it answers an empty 400, which the constitution forbids.
    /// </summary>
    public static IServiceCollection AddBadRequestProblemDetails(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
    }

    /// <summary>
    /// Adds <see cref="RequestIdMiddleware"/> and a last-resort exception handler, in that order, so even an unhandled
    /// failure answers with Problem Details and an <c>X-Request-Id</c>. Call before authentication.
    /// </summary>
    public static IApplicationBuilder UseRequestIdAndErrorHandling(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseMiddleware<RequestIdMiddleware>();
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            ExceptionHandler = context =>
            {
                // The exception handler cleared the response, headers included, before running this: put the request id back.
                if (RequestId.Get(context) is var id && id != Guid.Empty)
                {
                    context.Response.Headers[RequestId.HeaderName] = id.ToString();
                }

                var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                return ProblemResults.From(context, Map(exception)).ExecuteAsync(context);
            },
            // A body the client got wrong is a 400, not a server error: no Error log with a stack trace, whose JSON reader
            // message would also quote the offending character of the body (ADR-0010).
            SuppressDiagnosticsCallback = context => context.Exception is BadHttpRequestException,
        });
        return app;
    }

    // Exception text is never copied into the response: it could carry request content (ADR-0010).
    internal static Error Map(Exception? exception) => exception is BadHttpRequestException bad
        ? bad.StatusCode == StatusCodes.Status413PayloadTooLarge
            ? new Error(ErrorCode.PayloadTooLarge, "The request body is too large.")
            : new Error(ErrorCode.InvalidRequest, "The request could not be read.")
        : new Error(ErrorCode.InternalError, "An internal error occurred.");
}
