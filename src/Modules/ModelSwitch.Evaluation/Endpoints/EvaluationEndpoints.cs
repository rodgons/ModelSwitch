using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ModelSwitch.Evaluation.Application;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Http;
using ModelSwitch.SharedKernel.Pagination;

namespace ModelSwitch.Evaluation.Endpoints;

/// <summary>
/// Thin handlers for Datasets and Prompts (docs/api.md section 3, Evaluation). Viewers read and Operators write; the services check
/// the Tenant Grant and answer 404 for anything outside it.
/// </summary>
internal static class EvaluationEndpoints
{
    private const string _base = AdminAuthentication.AdminPathPrefix;

    public static IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup(_base);

        admin.MapGet("/datasets", ListDatasetsAsync).Produces<PagedResult<DatasetView>>().RequireAuthorization(AdminPolicies.Viewer);
        admin.MapPost("/datasets", CreateDatasetAsync).Produces<DatasetView>(StatusCodes.Status201Created).RequireAuthorization(AdminPolicies.Operator);
        admin.MapGet("/datasets/{datasetId:guid}", GetDatasetAsync).Produces<DatasetView>().RequireAuthorization(AdminPolicies.Viewer);
        admin.MapGet("/datasets/{datasetId:guid}/versions", ListDatasetVersionsAsync).Produces<PagedResult<DatasetVersionView>>().RequireAuthorization(AdminPolicies.Viewer);

        // The body is raw JSONL (application/x-ndjson), read by the service with a 10 MiB bound; it is not bound as JSON.
        admin.MapPost("/datasets/{datasetId:guid}/versions", UploadDatasetVersionAsync).Produces<DatasetVersionView>(StatusCodes.Status201Created).RequireAuthorization(AdminPolicies.Operator);
        admin.MapGet("/datasets/{datasetId:guid}/versions/{version:int}", GetDatasetVersionAsync).Produces<DatasetVersionView>().RequireAuthorization(AdminPolicies.Viewer);
        admin.MapGet("/datasets/{datasetId:guid}/versions/{version:int}/cases", ListCasesAsync).Produces<PagedResult<CaseView>>().RequireAuthorization(AdminPolicies.Viewer);

        admin.MapGet("/prompts", ListPromptsAsync).Produces<PagedResult<PromptView>>().RequireAuthorization(AdminPolicies.Viewer);
        admin.MapPost("/prompts", CreatePromptAsync).Produces<PromptView>(StatusCodes.Status201Created).RequireAuthorization(AdminPolicies.Operator);
        admin.MapGet("/prompts/{promptId:guid}", GetPromptAsync).Produces<PromptView>().RequireAuthorization(AdminPolicies.Viewer);
        admin.MapGet("/prompts/{promptId:guid}/versions", ListPromptVersionsAsync).Produces<PagedResult<PromptVersionView>>().RequireAuthorization(AdminPolicies.Viewer);
        admin.MapPost("/prompts/{promptId:guid}/versions", CreatePromptVersionAsync).Produces<PromptVersionView>(StatusCodes.Status201Created).RequireAuthorization(AdminPolicies.Operator);
        admin.MapGet("/prompts/{promptId:guid}/versions/{version:int}", GetPromptVersionAsync).Produces<PromptVersionView>().RequireAuthorization(AdminPolicies.Viewer);
        return app;
    }

    private static async Task<IResult> ListDatasetsAsync(HttpContext http, DatasetService datasets, string? tenantId, string? limit, string? cursor)
        => Json(http, await datasets.ListAsync(tenantId, limit, cursor, http.RequestAborted));

    private static async Task<IResult> CreateDatasetAsync(HttpContext http, CreateDatasetRequest? request, DatasetService datasets)
        => Respond(
            http,
            await datasets.CreateAsync(request ?? new CreateDatasetRequest(null, null, null), http.RequestAborted),
            dataset => Results.Created($"{_base}/datasets/{dataset.Id}", dataset));

    private static async Task<IResult> GetDatasetAsync(HttpContext http, Guid datasetId, DatasetService datasets)
        => Json(http, await datasets.GetAsync(datasetId, http.RequestAborted));

    private static async Task<IResult> ListDatasetVersionsAsync(HttpContext http, Guid datasetId, DatasetService datasets, string? limit, string? cursor)
        => Json(http, await datasets.ListVersionsAsync(datasetId, limit, cursor, http.RequestAborted));

    private static async Task<IResult> UploadDatasetVersionAsync(HttpContext http, Guid datasetId, DatasetService datasets)
        => Respond(
            http,
            await datasets.UploadVersionAsync(datasetId, new DatasetUpload(http.Request.ContentType, http.Request.ContentLength, http.Request.Body), http.RequestAborted),
            version => Results.Created($"{_base}/datasets/{datasetId}/versions/{version.Version}", version));

    private static async Task<IResult> GetDatasetVersionAsync(HttpContext http, Guid datasetId, int version, DatasetService datasets)
        => Json(http, await datasets.GetVersionAsync(datasetId, version, http.RequestAborted));

    private static async Task<IResult> ListCasesAsync(HttpContext http, Guid datasetId, int version, DatasetService datasets, string? limit, string? cursor)
        => Json(http, await datasets.ListCasesAsync(datasetId, version, limit, cursor, http.RequestAborted));

    private static async Task<IResult> ListPromptsAsync(HttpContext http, PromptService prompts, string? tenantId, string? limit, string? cursor)
        => Json(http, await prompts.ListAsync(tenantId, limit, cursor, http.RequestAborted));

    private static async Task<IResult> CreatePromptAsync(HttpContext http, CreatePromptRequest? request, PromptService prompts)
        => Respond(
            http,
            await prompts.CreateAsync(request ?? new CreatePromptRequest(null, null), http.RequestAborted),
            prompt => Results.Created($"{_base}/prompts/{prompt.Id}", prompt));

    private static async Task<IResult> GetPromptAsync(HttpContext http, Guid promptId, PromptService prompts)
        => Json(http, await prompts.GetAsync(promptId, http.RequestAborted));

    private static async Task<IResult> ListPromptVersionsAsync(HttpContext http, Guid promptId, PromptService prompts, string? limit, string? cursor)
        => Json(http, await prompts.ListVersionsAsync(promptId, limit, cursor, http.RequestAborted));

    private static async Task<IResult> CreatePromptVersionAsync(HttpContext http, Guid promptId, CreatePromptVersionRequest? request, PromptService prompts)
        => Respond(
            http,
            await prompts.CreateVersionAsync(promptId, request ?? new CreatePromptVersionRequest(null, null), http.RequestAborted),
            version => Results.Created($"{_base}/prompts/{promptId}/versions/{version.Version}", version));

    private static async Task<IResult> GetPromptVersionAsync(HttpContext http, Guid promptId, int version, PromptService prompts)
        => Json(http, await prompts.GetVersionAsync(promptId, version, http.RequestAborted));

    private static IResult Json<T>(HttpContext http, Result<T> result)
        => result.IsFailure ? ProblemResults.From(http, result.Error) : Results.Json(result.Value);

    private static IResult Respond<T>(HttpContext http, Result<T> result, Func<T, IResult> success)
        => result.IsFailure ? ProblemResults.From(http, result.Error) : success(result.Value);
}
