using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Registry.Application;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Http;
using ModelSwitch.SharedKernel.Pagination;

namespace ModelSwitch.Registry.Endpoints;

/// <summary>
/// Thin handlers for the Provider, Provider Model and Price admin endpoints (docs/api.md section 3). Reads are open to any
/// Administrator (the catalog holds no tenant data and no secret); writes are Owner only.
/// </summary>
internal static class CatalogEndpoints
{
    private static readonly string _base = AdminAuthentication.AdminPathPrefix;

    public static IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup(_base);

        admin.MapGet("/providers", ListProvidersAsync).Produces<PagedResult<ProviderView>>().RequireAuthorization(AdminPolicies.Viewer);
        admin.MapPost("/providers", CreateProviderAsync).Produces<ProviderView>(StatusCodes.Status201Created).RequireAuthorization(AdminPolicies.Owner);
        admin.MapGet("/providers/{providerId:guid}", GetProviderAsync).Produces<ProviderView>().RequireAuthorization(AdminPolicies.Viewer);
        admin.MapPatch("/providers/{providerId:guid}", UpdateProviderAsync).Produces<ProviderView>().RequireAuthorization(AdminPolicies.Owner);
        admin.MapPost("/providers/{providerId:guid}/connectivity-check", CheckConnectivityAsync).Produces<ConnectivityView>().RequireAuthorization(AdminPolicies.Owner);

        admin.MapGet("/providers/{providerId:guid}/models", ListModelsAsync).Produces<PagedResult<ProviderModelView>>().RequireAuthorization(AdminPolicies.Viewer);
        admin.MapPost("/providers/{providerId:guid}/models", CreateModelAsync).Produces<ProviderModelView>(StatusCodes.Status201Created).RequireAuthorization(AdminPolicies.Owner);
        admin.MapPatch("/models/{modelId:guid}", UpdateModelAsync).Produces<ProviderModelView>().RequireAuthorization(AdminPolicies.Owner);

        admin.MapGet("/models/{modelId:guid}/prices", ListPricesAsync).Produces<PagedResult<ModelPriceView>>().RequireAuthorization(AdminPolicies.Viewer);
        admin.MapPost("/models/{modelId:guid}/prices", CreatePriceAsync).Produces<ModelPriceView>(StatusCodes.Status201Created).RequireAuthorization(AdminPolicies.Owner);
        return app;
    }

    private static async Task<IResult> ListProvidersAsync(HttpContext http, ProviderAdminService providers, string? limit, string? cursor)
        => Json(http, await providers.ListAsync(limit, cursor, http.RequestAborted));

    private static async Task<IResult> GetProviderAsync(HttpContext http, Guid providerId, ProviderAdminService providers)
        => Json(http, await providers.GetAsync(providerId, http.RequestAborted));

    private static async Task<IResult> CreateProviderAsync(HttpContext http, CreateProviderRequest? request, ProviderAdminService providers)
    {
        var result = await providers.CreateAsync(request ?? new CreateProviderRequest(null, null, null, null, null, null), http.RequestAborted);
        return result.IsFailure
            ? ProblemResults.From(http, result.Error)
            : Results.Created($"{_base}/providers/{result.Value.Id}", result.Value);
    }

    private static async Task<IResult> UpdateProviderAsync(HttpContext http, Guid providerId, UpdateProviderRequest? request, ProviderAdminService providers)
        => Json(http, await providers.UpdateAsync(providerId, request ?? new UpdateProviderRequest(null, null, null, null), http.RequestAborted));

    private static async Task<IResult> CheckConnectivityAsync(HttpContext http, Guid providerId, ProviderAdminService providers)
        => Json(http, await providers.CheckConnectivityAsync(providerId, http.RequestAborted));

    private static async Task<IResult> ListModelsAsync(HttpContext http, Guid providerId, ProviderModelAdminService models, string? limit, string? cursor)
        => Json(http, await models.ListAsync(providerId, limit, cursor, http.RequestAborted));

    private static async Task<IResult> CreateModelAsync(HttpContext http, Guid providerId, CreateProviderModelRequest? request, ProviderModelAdminService models)
    {
        var result = await models.CreateAsync(providerId, request ?? new CreateProviderModelRequest(null, null, null, null), http.RequestAborted);
        return result.IsFailure
            ? ProblemResults.From(http, result.Error)
            : Results.Created($"{_base}/providers/{providerId}/models", result.Value);
    }

    private static async Task<IResult> UpdateModelAsync(HttpContext http, Guid modelId, UpdateProviderModelRequest? request, ProviderModelAdminService models)
        => Json(http, await models.UpdateAsync(modelId, request ?? new UpdateProviderModelRequest(null, null, null), http.RequestAborted));

    private static async Task<IResult> ListPricesAsync(HttpContext http, Guid modelId, ModelPriceAdminService prices, string? limit, string? cursor)
        => Json(http, await prices.ListAsync(modelId, limit, cursor, http.RequestAborted));

    private static async Task<IResult> CreatePriceAsync(HttpContext http, Guid modelId, CreateModelPriceRequest? request, ModelPriceAdminService prices)
    {
        var result = await prices.CreateAsync(modelId, request ?? new CreateModelPriceRequest(null, null, null, null, null), http.RequestAborted);
        return result.IsFailure
            ? ProblemResults.From(http, result.Error)
            : Results.Created($"{_base}/models/{modelId}/prices", result.Value);
    }

    private static IResult Json<T>(HttpContext http, Result<T> result)
        => result.IsFailure ? ProblemResults.From(http, result.Error) : Results.Json(result.Value);
}
