using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Inference.Application;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Http;

namespace ModelSwitch.Inference.Endpoints;

/// <summary>Thin handlers for the inference API: bind, call one application service, map the Result (architecture.md section 3).</summary>
internal static class InferenceEndpoints
{
    public static IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1");

        group.MapPost("/chat/completions", CompleteAsync).RequireAuthorization(ApiKeyClaims.InferencePolicy);
        group.MapGet("/models", ListModels).RequireAuthorization(ApiKeyClaims.ModelsReadPolicy);

        return app;
    }

    private static async Task<IResult> CompleteAsync(HttpContext http, ChatCompletionsService chat, InferenceOptions options, TimeProvider time)
    {
        var tenant = ResolveTenant(http);
        if (tenant.IsFailure)
        {
            return ProblemResults.From(http, tenant.Error);
        }

        var timeout = RoutingHeaders.ValidateTimeout(http.Request.Headers[RoutingHeaders.TimeoutHeaderName].FirstOrDefault());
        if (timeout.IsFailure)
        {
            return ProblemResults.From(http, timeout.Error);
        }

        var caller = new ChatCaller(
            RequestId.Get(http),
            tenant.Value,
            ApiKeyClaims.FindApiKeyId(http.User),
            http.Request.Headers[ClientRequestIds.HeaderName].FirstOrDefault())
        {
            TaskType = http.Request.Headers[RoutingHeaders.TaskHeaderName].FirstOrDefault(),
            Timeout = timeout.Value,
        };
        var result = await chat.CompleteAsync(caller, http.Request.Body, http.RequestAborted);
        if (result.IsFailure)
        {
            return ProblemResults.From(http, result.Error);
        }

        var reply = result.Value;
        var created = time.GetUtcNow().ToUnixTimeSeconds();
        return reply.Stream is { } stream
            ? await StreamAsync(http, stream, reply.IncludeUsage, options, time, created)
            : Results.Json(ChatCompletionWire.ChatCompletion(reply.Completion!, created));
    }

    // The First Token is in hand, so from here the response is a 200 event stream: failures travel inside it (FR-05).
    private static async Task<IResult> StreamAsync(HttpContext http, StreamingCompletion stream, bool includeUsage, InferenceOptions options, TimeProvider time, long created)
    {
        await using (stream)
        {
            var response = http.Response;
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";
            response.Headers["X-Accel-Buffering"] = "no";
            try
            {
                await new SsePump(time, options.KeepAliveInterval).PumpAsync(response.BodyWriter, stream, includeUsage, created, http.RequestAborted);
            }
            catch (Exception exception) when (http.RequestAborted.IsCancellationRequested && exception is OperationCanceledException or IOException)
            {
                // The client disconnected: nothing to answer. The stream already aborted the Provider call and settled the request.
            }
        }

        return Results.Empty;
    }

    private static IResult ListModels(HttpContext http, ModelListService models)
    {
        var tenant = ResolveTenant(http);
        if (tenant.IsFailure)
        {
            return ProblemResults.From(http, tenant.Error);
        }

        return Results.Json(ChatCompletionWire.ModelList(models.ListFor(tenant.Value)));
    }

    // The authorization policy already rejected anonymous callers; a principal without a tenant claim is still not a valid key.
    private static Result<Guid> ResolveTenant(HttpContext http)
        => ApiKeyClaims.FindTenantId(http.User) is { } tenantId
            ? Result.Success(tenantId)
            : Result.Failure<Guid>(new Error(ErrorCode.InvalidApiKey, "The API key is missing or invalid."));
}
