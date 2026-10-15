using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Inference.Application;
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

    private static async Task<IResult> CompleteAsync(HttpContext http, ChatCompletionsService chat, TimeProvider time)
    {
        var tenant = ResolveTenant(http);
        if (tenant.IsFailure)
        {
            return ProblemResults.From(http, tenant.Error);
        }

        var caller = new ChatCaller(
            RequestId.Get(http),
            tenant.Value,
            ApiKeyClaims.FindApiKeyId(http.User),
            http.Request.Headers[ClientRequestIds.HeaderName].FirstOrDefault())
        {
            TaskType = http.Request.Headers[RoutingHeaders.TaskHeaderName].FirstOrDefault(),
            TimeoutMs = http.Request.Headers[RoutingHeaders.TimeoutHeaderName].FirstOrDefault(),
        };
        var result = await chat.CompleteAsync(caller, http.Request.Body, http.RequestAborted);
        return result.IsSuccess
            ? Results.Json(ChatCompletionWire.ChatCompletion(result.Value, time.GetUtcNow().ToUnixTimeSeconds()))
            : ProblemResults.From(http, result.Error);
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
