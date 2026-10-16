using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Routing.Application;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Http;

namespace ModelSwitch.Routing.Endpoints;

/// <summary>Thin handlers for the Routing Policy and alias admin endpoints (docs/api.md section 3). Reads: Viewer. Writes: Owner.</summary>
internal static class RoutingAdminEndpoints
{
    private static readonly string _base = AdminAuthentication.AdminPathPrefix;

    public static IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup(_base);

        admin.MapGet("/routing-policies", ListAsync).RequireAuthorization(AdminPolicies.Viewer);
        admin.MapPost("/routing-policies", CreateAsync).RequireAuthorization(AdminPolicies.Owner);
        admin.MapPost("/routing-policies/validate", ValidateAsync).RequireAuthorization(AdminPolicies.Owner);
        admin.MapPut("/routing-policies/{policyId:guid}", UpdateAsync).RequireAuthorization(AdminPolicies.Owner);
        admin.MapGet("/routing-policies/{policyId:guid}/versions", ListVersionsAsync).RequireAuthorization(AdminPolicies.Viewer);
        admin.MapGet("/routing-policies/{policyId:guid}/versions/{version:int}", GetVersionAsync).RequireAuthorization(AdminPolicies.Viewer);

        admin.MapGet("/aliases/{alias}", GetAliasAsync).RequireAuthorization(AdminPolicies.Viewer);
        admin.MapPut("/aliases/{alias}", BindAliasAsync).RequireAuthorization(AdminPolicies.Owner);
        admin.MapDelete("/aliases/{alias}", UnbindAliasAsync).RequireAuthorization(AdminPolicies.Owner);
        return app;
    }

    private static async Task<IResult> ListAsync(HttpContext http, PolicyAdminService policies, string? limit, string? cursor)
        => Json(http, await policies.ListAsync(limit, cursor, http.RequestAborted));

    private static async Task<IResult> CreateAsync(HttpContext http, CreatePolicyRequest? request, PolicyAdminService policies)
    {
        var result = await policies.CreateAsync(request ?? new CreatePolicyRequest(null, null), http.RequestAborted);
        return result.IsFailure
            ? ProblemResults.From(http, result.Error)
            : Results.Created($"{_base}/routing-policies/{result.Value.PolicyId}/versions/{result.Value.Version}", result.Value);
    }

    private static async Task<IResult> ValidateAsync(HttpContext http, ValidatePolicyRequest? request, PolicyAdminService policies)
        => Json(http, await policies.ValidateAsync(request ?? new ValidatePolicyRequest(null), http.RequestAborted));

    private static async Task<IResult> UpdateAsync(HttpContext http, Guid policyId, UpdatePolicyRequest? request, PolicyAdminService policies)
    {
        var result = await policies.CreateVersionAsync(policyId, request ?? new UpdatePolicyRequest(null, null), http.RequestAborted);
        return result.IsFailure
            ? ProblemResults.From(http, result.Error)
            : Results.Json(result.Value, statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> ListVersionsAsync(HttpContext http, Guid policyId, PolicyAdminService policies, string? limit, string? cursor)
        => Json(http, await policies.ListVersionsAsync(policyId, limit, cursor, http.RequestAborted));

    private static async Task<IResult> GetVersionAsync(HttpContext http, Guid policyId, int version, PolicyAdminService policies)
        => Json(http, await policies.GetVersionAsync(policyId, version, http.RequestAborted));

    private static async Task<IResult> GetAliasAsync(HttpContext http, string alias, AliasAdminService aliases)
        => Json(http, await aliases.GetAsync(alias, http.RequestAborted));

    private static async Task<IResult> BindAliasAsync(HttpContext http, string alias, BindAliasRequest? request, AliasAdminService aliases)
    {
        var result = await aliases.SaveAsync(alias, request ?? new BindAliasRequest(null, null), http.RequestAborted);
        if (result.IsFailure)
        {
            return ProblemResults.From(http, result.Error);
        }

        return result.Value.Created
            ? Results.Created($"{_base}/aliases/{alias}", result.Value.View)
            : Results.Json(result.Value.View);
    }

    private static async Task<IResult> UnbindAliasAsync(HttpContext http, string alias, AliasAdminService aliases)
    {
        var result = await aliases.UnbindAsync(alias, http.RequestAborted);
        return result.IsFailure ? ProblemResults.From(http, result.Error) : Results.NoContent();
    }

    private static IResult Json<T>(HttpContext http, Result<T> result)
        => result.IsFailure ? ProblemResults.From(http, result.Error) : Results.Json(result.Value);
}
