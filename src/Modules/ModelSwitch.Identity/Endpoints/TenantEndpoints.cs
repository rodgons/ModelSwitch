using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ModelSwitch.Identity.Application;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel.Http;

namespace ModelSwitch.Identity.Endpoints;

/// <summary>
/// Thin handlers for Tenants, limits, model access and API Keys (docs/api.md section 3). Viewers read and Operators write; the
/// Tenant Grant is checked by the services, which answer 404 for a Tenant outside it.
/// </summary>
internal static class TenantEndpoints
{
    private const string _base = AdminAuthentication.AdminPathPrefix + "/tenants";

    public static IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        var tenants = app.MapGroup(AdminAuthentication.AdminPathPrefix);

        tenants.MapGet("/tenants", ListAsync).RequireAuthorization(AdminPolicies.Viewer);
        tenants.MapPost("/tenants", CreateAsync).RequireAuthorization(AdminPolicies.Operator);
        tenants.MapGet("/tenants/{tenantId:guid}", GetAsync).RequireAuthorization(AdminPolicies.Viewer);
        tenants.MapPatch("/tenants/{tenantId:guid}", UpdateAsync).RequireAuthorization(AdminPolicies.Operator);
        tenants.MapPut("/tenants/{tenantId:guid}/limits", SetLimitsAsync).RequireAuthorization(AdminPolicies.Operator);
        tenants.MapPut("/tenants/{tenantId:guid}/model-access", SetModelAccessAsync).RequireAuthorization(AdminPolicies.Operator);

        tenants.MapGet("/tenants/{tenantId:guid}/api-keys", ListKeysAsync).RequireAuthorization(AdminPolicies.Viewer);
        tenants.MapPost("/tenants/{tenantId:guid}/api-keys", CreateKeyAsync).RequireAuthorization(AdminPolicies.Operator);
        tenants.MapDelete("/tenants/{tenantId:guid}/api-keys/{keyId:guid}", RevokeKeyAsync).RequireAuthorization(AdminPolicies.Operator);
        return app;
    }

    private static async Task<IResult> ListAsync(HttpContext http, TenantService tenants, string? limit, string? cursor)
        => Respond(http, await tenants.ListAsync(limit, cursor, http.RequestAborted), page => Results.Json(new { items = page.Items, nextCursor = page.NextCursor }));

    private static async Task<IResult> CreateAsync(HttpContext http, CreateTenantRequest? request, TenantService tenants)
        => Respond(
            http,
            await tenants.CreateAsync(request ?? new CreateTenantRequest(null), http.RequestAborted),
            tenant => Results.Created($"{_base}/{tenant.Id}", tenant));

    private static async Task<IResult> GetAsync(HttpContext http, Guid tenantId, TenantService tenants)
        => Respond(http, await tenants.GetAsync(tenantId, http.RequestAborted), tenant => Results.Json(tenant));

    private static async Task<IResult> UpdateAsync(HttpContext http, Guid tenantId, UpdateTenantRequest? request, TenantService tenants)
        => Respond(http, await tenants.UpdateAsync(tenantId, request ?? new UpdateTenantRequest(null, null), http.RequestAborted), tenant => Results.Json(tenant));

    private static async Task<IResult> SetLimitsAsync(HttpContext http, Guid tenantId, UpdateLimitsRequest? request, TenantService tenants)
        => Respond(http, await tenants.SetLimitsAsync(tenantId, request ?? new UpdateLimitsRequest(null, null, null, null), http.RequestAborted), limits => Results.Json(limits));

    private static async Task<IResult> SetModelAccessAsync(HttpContext http, Guid tenantId, UpdateModelAccessRequest? request, TenantService tenants)
        => Respond(http, await tenants.SetModelAccessAsync(tenantId, request ?? new UpdateModelAccessRequest(null), http.RequestAborted), access => Results.Json(access));

    private static async Task<IResult> ListKeysAsync(HttpContext http, Guid tenantId, ApiKeyAdminService keys, string? limit, string? cursor)
        => Respond(http, await keys.ListAsync(tenantId, limit, cursor, http.RequestAborted), page => Results.Json(new { items = page.Items, nextCursor = page.NextCursor }));

    private static async Task<IResult> CreateKeyAsync(HttpContext http, Guid tenantId, CreateApiKeyRequest? request, ApiKeyAdminService keys)
    {
        var result = await keys.CreateAsync(tenantId, request ?? new CreateApiKeyRequest(null, null, null), http.RequestAborted);

        // The raw key is shown exactly once; nothing may cache the response (security.md section 2).
        http.Response.Headers.CacheControl = "no-store";
        return Respond(
            http,
            result,
            created => Results.Created($"{_base}/{tenantId}/api-keys/{created.Id}", created));
    }

    private static async Task<IResult> RevokeKeyAsync(HttpContext http, Guid tenantId, Guid keyId, ApiKeyAdminService keys)
    {
        var result = await keys.RevokeAsync(tenantId, keyId, http.RequestAborted);
        return result.IsFailure ? ProblemResults.From(http, result.Error) : Results.NoContent();
    }

    private static IResult Respond<T>(HttpContext http, ModelSwitch.SharedKernel.Result<T> result, Func<T, IResult> success)
        => result.IsFailure ? ProblemResults.From(http, result.Error) : success(result.Value);
}
