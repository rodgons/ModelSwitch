using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Inference.Application;
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
        if (ApiKeyClaims.FindTenantId(http.User) is not { } tenantId)
        {
            return ProblemResults.From(http, new Error(ErrorCode.InvalidApiKey, "The API key is missing or invalid."));
        }

        var result = await chat.CompleteAsync(RequestId.Get(http), tenantId, http.Request.Body, http.RequestAborted);
        return result.IsSuccess
            ? Results.Json(ChatCompletionWire.ChatCompletion(result.Value, time.GetUtcNow().ToUnixTimeSeconds()))
            : ProblemResults.From(http, result.Error);
    }

    private static IResult ListModels(HttpContext http, ModelListService models)
    {
        if (ApiKeyClaims.FindTenantId(http.User) is not { } tenantId)
        {
            return ProblemResults.From(http, new Error(ErrorCode.InvalidApiKey, "The API key is missing or invalid."));
        }

        return Results.Json(ChatCompletionWire.ModelList(models.ListFor(tenantId)));
    }
}
