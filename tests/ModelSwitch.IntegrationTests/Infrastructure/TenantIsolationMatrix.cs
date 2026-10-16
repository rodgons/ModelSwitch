using System.Net;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Infrastructure;

/// <summary>The ids of the other Tenant's resources the matrix aims at; add a property when a new kind of tenant-scoped id appears.</summary>
public sealed record ForeignIds(Guid TenantId, Guid ApiKeyId);

/// <summary>One tenant-scoped admin endpoint: the verb, the path built from <see cref="ForeignIds"/> and a valid body for writes.</summary>
public sealed record TenantScopedRoute(string Name, HttpMethod Method, Func<ForeignIds, string> Path, Func<object?>? Body = null);

/// <summary>
/// The tenant-isolation matrix of testing.md section 2 (security.md section 4): every tenant-scoped admin endpoint, called as an
/// Administrator whose grant excludes the Tenant, must answer 404 and change nothing. Each task that adds a tenant-scoped admin
/// endpoint appends it to <see cref="Routes"/>; <c>TenantIsolationTests</c> runs them all.
/// </summary>
public static class TenantIsolationMatrix
{
    public static IReadOnlyList<TenantScopedRoute> Routes { get; } =
    [
        new("GET tenant", HttpMethod.Get, ids => $"/admin/tenants/{ids.TenantId}"),
        new("PATCH tenant", HttpMethod.Patch, ids => $"/admin/tenants/{ids.TenantId}", () => new { status = "suspended" }),
        new("PUT limits", HttpMethod.Put, ids => $"/admin/tenants/{ids.TenantId}/limits", () => new { requestsPerMinute = 1 }),
        new("PUT model access", HttpMethod.Put, ids => $"/admin/tenants/{ids.TenantId}/model-access", () => new { allowedAliases = new[] { "*" } }),
        new("GET api keys", HttpMethod.Get, ids => $"/admin/tenants/{ids.TenantId}/api-keys"),
        new("POST api key", HttpMethod.Post, ids => $"/admin/tenants/{ids.TenantId}/api-keys", () => new { name = "intruder" }),
        new("DELETE api key", HttpMethod.Delete, ids => $"/admin/tenants/{ids.TenantId}/api-keys/{ids.ApiKeyId}"),
    ];

    /// <summary>
    /// Calls every route in <see cref="Routes"/> as <paramref name="outsider"/> against <paramref name="foreign"/> and expects 404 with
    /// the <c>not_found</c> code. Write routes use a valid body, so a 404 can only come from the grant check.
    /// </summary>
    public static async Task AssertAllForeignRoutesAreNotFoundAsync(AdminBrowser outsider, ForeignIds foreign)
    {
        ArgumentNullException.ThrowIfNull(outsider);
        ArgumentNullException.ThrowIfNull(foreign);
        foreach (var route in Routes)
        {
            using var response = await outsider.SendJsonAsync(route.Method, route.Path(foreign), route.Body?.Invoke());
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{route.Name} must hide a Tenant outside the grant");
            (await AdminBrowser.ErrorCodeAsync(response)).ShouldBe("not_found", route.Name);
        }
    }
}
