using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ModelSwitch.Admin.Application;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel.Http;

namespace ModelSwitch.Admin.Endpoints;

/// <summary>
/// Thin handlers for the dashboard's read endpoints (api.md section 3, "Requests and usage"): bind the query string, call
/// <see cref="DashboardService"/>, map the Result. Every Administrator may read; the Tenant Grant narrows what they see.
/// </summary>
internal static class DashboardEndpoints
{
    public static IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup(AdminAuthentication.AdminPathPrefix).RequireAuthorization(AdminPolicies.Viewer);

        admin.MapGet("/overview", OverviewAsync).Produces<OverviewView>();
        admin.MapGet("/requests", ListRequestsAsync).Produces<RequestPage>();
        admin.MapGet("/requests/{requestId:guid}", GetRequestAsync).Produces<RequestDetailView>();
        admin.MapGet("/usage", UsageAsync).Produces<UsageReportView>();
        admin.MapGet("/provider-health", ProviderHealthAsync).Produces<ProviderHealthView>();
        return app;
    }

    private static async Task<IResult> OverviewAsync(HttpContext http, DashboardService dashboard)
        => Map(http, await dashboard.OverviewAsync(Query(http.Request.Query), http.RequestAborted));

    private static async Task<IResult> ListRequestsAsync(HttpContext http, DashboardService dashboard)
        => Map(http, await dashboard.ListRequestsAsync(Query(http.Request.Query), http.RequestAborted));

    private static async Task<IResult> GetRequestAsync(HttpContext http, Guid requestId, DashboardService dashboard)
        => Map(http, await dashboard.GetRequestAsync(requestId, http.RequestAborted));

    private static async Task<IResult> UsageAsync(HttpContext http, DashboardService dashboard)
        => Map(http, await dashboard.UsageAsync(Query(http.Request.Query), http.RequestAborted));

    private static async Task<IResult> ProviderHealthAsync(HttpContext http, DashboardService dashboard)
        => Results.Json(await dashboard.ProviderHealthAsync(http.RequestAborted));

    private static IResult Map<T>(HttpContext http, SharedKernel.Result<T> result)
        => result.IsSuccess ? Results.Json(result.Value) : ProblemResults.From(http, result.Error);

    private static DashboardQueryString Query(IQueryCollection query) => new()
    {
        From = query["from"].FirstOrDefault(),
        To = query["to"].FirstOrDefault(),
        TenantId = query["tenantId"].FirstOrDefault(),
        Origin = query["origin"].FirstOrDefault(),
        Alias = query["alias"].FirstOrDefault(),
        ProviderId = query["providerId"].FirstOrDefault(),
        ModelId = query["modelId"].FirstOrDefault(),
        Status = query["status"].FirstOrDefault(),
        ErrorCode = query["errorCode"].FirstOrDefault(),
        HadFallback = query["hadFallback"].FirstOrDefault(),
        Sort = query["sort"].FirstOrDefault(),
        Order = query["order"].FirstOrDefault(),
        Limit = query["limit"].FirstOrDefault(),
        Cursor = query["cursor"].FirstOrDefault(),
        GroupBy = query["groupBy"].Where(v => v is not null).Select(v => v!).ToList(),
        Interval = query["interval"].FirstOrDefault(),
    };
}
