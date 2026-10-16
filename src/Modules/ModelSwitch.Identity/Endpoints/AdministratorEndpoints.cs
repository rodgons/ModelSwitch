using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ModelSwitch.Identity.Application;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Http;

namespace ModelSwitch.Identity.Endpoints;

internal sealed record SignInRequest(string? Email, string? Password);

internal sealed record AdministratorSummary(Guid Id, string Email, string Role, bool AllTenants, IReadOnlyList<Guid> TenantIds);

internal sealed record SessionResponse(AdministratorSummary Administrator, string AntiforgeryToken);

internal sealed record AntiforgeryTokenResponse(string Token);

internal sealed record AdministratorList(IReadOnlyList<AdministratorView> Items, string? NextCursor);

/// <summary>
/// Thin handlers for the admin session and the Administrator and audit endpoints (docs/api.md section 3). The Role is the
/// route's policy; cookie authentication and antiforgery validation happen in the Api host's pipeline.
/// </summary>
internal static class AdministratorEndpoints
{
    public static IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup(AdminAuthentication.AdminPathPrefix);

        // Anonymous, so the sign-in form can fetch the token it must send with POST /admin/session.
        admin.MapGet("/antiforgery-token", GetAntiforgeryToken).Produces<AntiforgeryTokenResponse>().AllowAnonymous();

        admin.MapPost("/session", SignInAsync).Produces<SessionResponse>().AllowAnonymous();
        admin.MapGet("/session", GetSession).Produces<SessionResponse>().RequireAuthorization(AdminPolicies.Viewer);
        admin.MapDelete("/session", SignOutAsync).Produces(StatusCodes.Status204NoContent).RequireAuthorization(AdminPolicies.Viewer);

        admin.MapGet("/administrators", ListAdministratorsAsync).Produces<AdministratorList>().RequireAuthorization(AdminPolicies.Owner);
        admin.MapPost("/administrators", CreateAdministratorAsync).Produces<AdministratorView>(StatusCodes.Status201Created).RequireAuthorization(AdminPolicies.Owner);
        admin.MapPatch("/administrators/{administratorId:guid}", UpdateAdministratorAsync).Produces<AdministratorView>().RequireAuthorization(AdminPolicies.Owner);

        admin.MapGet("/audit-records", ListAuditRecordsAsync).Produces<AuditPage>().RequireAuthorization(AdminPolicies.Owner);
        return app;
    }

    private static IResult GetAntiforgeryToken(HttpContext http, IAntiforgery antiforgery)
    {
        NoStore(http);
        return Results.Json(new AntiforgeryTokenResponse(antiforgery.GetAndStoreTokens(http).RequestToken!));
    }

    private static async Task<IResult> SignInAsync(HttpContext http, SignInRequest? request, IAdministratorSignIn signIn, IAntiforgery antiforgery)
    {
        var result = await signIn.SignInAsync(request?.Email, request?.Password, http.RequestAborted);
        if (result.IsFailure)
        {
            return ProblemResults.From(http, result.Error);
        }

        var principal = AdminClaims.ToPrincipal(result.Value);
        await http.SignInAsync(AdminAuthentication.Scheme, principal);

        // The token is bound to the signed-in Administrator, so it must be issued for the new principal.
        http.User = principal;
        NoStore(http);
        return Results.Json(new SessionResponse(Summarise(result.Value), antiforgery.GetAndStoreTokens(http).RequestToken!));
    }

    private static IResult GetSession(HttpContext http, ICurrentAdministrator current, IAntiforgery antiforgery)
    {
        var administrator = current.Current;
        if (administrator is null)
        {
            return ProblemResults.From(http, new Error(ErrorCode.Unauthenticated, "Sign in to continue."));
        }

        NoStore(http);
        return Results.Json(new SessionResponse(Summarise(administrator), antiforgery.GetAndStoreTokens(http).RequestToken!));
    }

    private static async Task<IResult> SignOutAsync(HttpContext http, IAuditWriter audit)
    {
        var administrator = AdminClaims.FromPrincipal(http.User);
        await http.SignOutAsync(AdminAuthentication.Scheme);
        if (administrator is not null)
        {
            await audit.WriteAsync(
                new AuditEntry(AuditActions.AdministratorSignedOut, "administrator", administrator.Id.ToString()) { ActorAdministratorId = administrator.Id },
                http.RequestAborted);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> ListAdministratorsAsync(AdministratorService administrators, HttpContext http)
        => Results.Json(new AdministratorList(await administrators.ListAsync(http.RequestAborted), NextCursor: null));

    private static async Task<IResult> CreateAdministratorAsync(HttpContext http, CreateAdministratorRequest? request, AdministratorService administrators)
    {
        var result = await administrators.CreateAsync(request ?? new CreateAdministratorRequest(null, null, null, null, null), http.RequestAborted);
        return result.IsFailure
            ? ProblemResults.From(http, result.Error)
            : Results.Created($"{AdminAuthentication.AdminPathPrefix}/administrators/{result.Value.Id}", result.Value);
    }

    private static async Task<IResult> UpdateAdministratorAsync(HttpContext http, Guid administratorId, UpdateAdministratorRequest? request, AdministratorService administrators)
    {
        var result = await administrators.UpdateAsync(administratorId, request ?? new UpdateAdministratorRequest(null, null, null, null, null), http.RequestAborted);
        return result.IsFailure ? ProblemResults.From(http, result.Error) : Results.Json(result.Value);
    }

    private static async Task<IResult> ListAuditRecordsAsync(HttpContext http, AuditQueryService audit, string? limit, string? cursor, string? tenantId, string? action, string? from, string? to)
    {
        var result = await audit.ListAsync(new AuditQueryRequest(limit, cursor, tenantId, action, from, to), http.RequestAborted);
        return result.IsFailure ? ProblemResults.From(http, result.Error) : Results.Json(result.Value);
    }

    private static AdministratorSummary Summarise(AdministratorPrincipal administrator) => new(
        administrator.Id,
        administrator.Email,
        AdministratorRoles.ToWire(administrator.Role),
        administrator.Grant.IsAll,
        [.. administrator.Grant.TenantIds.Order()]);

    private static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";
}
