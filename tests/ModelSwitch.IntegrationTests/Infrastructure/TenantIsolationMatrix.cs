using System.Net;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Infrastructure;

/// <summary>The ids of the other Tenant's resources the matrix aims at; add a property when a new kind of tenant-scoped id appears.</summary>
public sealed record ForeignIds(Guid TenantId, Guid ApiKeyId, Guid RequestId = default, Guid DatasetId = default, Guid PromptId = default);

/// <summary>
/// One tenant-scoped admin endpoint: the verb, the path built from <see cref="ForeignIds"/> and a valid body for writes (an
/// <see cref="HttpContent"/> is sent as is, anything else as JSON). <see cref="ByQuery"/> marks a route that takes the Tenant as a
/// <c>tenantId</c> query filter or body field rather than a route parameter, which the route-parameter coverage gate cannot see.
/// </summary>
public sealed record TenantScopedRoute(string Pattern, HttpMethod Method, Func<ForeignIds, string> Path, Func<ForeignIds, object?>? Body = null, bool ByQuery = false)
{
    public string Name => $"{Method.Method} {Pattern}";

    /// <summary>True when the path carries an API Key id, so the key can be aimed at under the caller's own Tenant.</summary>
    public bool HasChildId => Path(new ForeignIds(Guid.Empty, Guid.Empty)) != Path(new ForeignIds(Guid.Empty, Guid.AllBitsSet));
}

/// <summary>
/// The tenant-isolation matrix of testing.md section 2 (security.md section 4): every tenant-scoped admin endpoint, called as an
/// Administrator whose grant excludes the Tenant, must answer 404 and change nothing. Each task that adds a tenant-scoped admin
/// endpoint appends it to <see cref="Routes"/>; <c>TenantIsolationTests</c> runs them all.
/// </summary>
public static class TenantIsolationMatrix
{
    public static IReadOnlyList<TenantScopedRoute> Routes { get; } =
    [
        new("/admin/tenants/{tenantId:guid}", HttpMethod.Get, ids => $"/admin/tenants/{ids.TenantId}"),
        new("/admin/tenants/{tenantId:guid}", HttpMethod.Patch, ids => $"/admin/tenants/{ids.TenantId}", _ => new { status = "suspended" }),
        new("/admin/tenants/{tenantId:guid}/limits", HttpMethod.Put, ids => $"/admin/tenants/{ids.TenantId}/limits", _ => new { requestsPerMinute = 1 }),
        new("/admin/tenants/{tenantId:guid}/model-access", HttpMethod.Put, ids => $"/admin/tenants/{ids.TenantId}/model-access", _ => new { allowedAliases = new[] { "*" } }),
        new("/admin/tenants/{tenantId:guid}/api-keys", HttpMethod.Get, ids => $"/admin/tenants/{ids.TenantId}/api-keys"),
        new("/admin/tenants/{tenantId:guid}/api-keys", HttpMethod.Post, ids => $"/admin/tenants/{ids.TenantId}/api-keys", _ => new { name = "intruder" }),
        new("/admin/tenants/{tenantId:guid}/api-keys/{keyId:guid}", HttpMethod.Delete, ids => $"/admin/tenants/{ids.TenantId}/api-keys/{ids.ApiKeyId}"),
        new("/admin/requests/{requestId:guid}", HttpMethod.Get, ids => $"/admin/requests/{ids.RequestId}"),
        new("/admin/requests", HttpMethod.Get, ids => $"/admin/requests?tenantId={ids.TenantId}", ByQuery: true),
        new("/admin/usage", HttpMethod.Get, ids => $"/admin/usage?tenantId={ids.TenantId}", ByQuery: true),
        new("/admin/overview", HttpMethod.Get, ids => $"/admin/overview?tenantId={ids.TenantId}", ByQuery: true),
        new("/admin/datasets", HttpMethod.Get, ids => $"/admin/datasets?tenantId={ids.TenantId}", ByQuery: true),
        new("/admin/datasets", HttpMethod.Post, _ => "/admin/datasets", ids => new { tenantId = ids.TenantId, name = "intruder", taskKind = "classification" }, ByQuery: true),
        new("/admin/datasets/{datasetId:guid}", HttpMethod.Get, ids => $"/admin/datasets/{ids.DatasetId}"),
        new("/admin/datasets/{datasetId:guid}/versions", HttpMethod.Get, ids => $"/admin/datasets/{ids.DatasetId}/versions"),
        new("/admin/datasets/{datasetId:guid}/versions", HttpMethod.Post, ids => $"/admin/datasets/{ids.DatasetId}/versions", _ => Jsonl("{\"id\":\"x\",\"input\":{},\"expected\":\"spam\"}")),
        new("/admin/datasets/{datasetId:guid}/versions/{version:int}", HttpMethod.Get, ids => $"/admin/datasets/{ids.DatasetId}/versions/1"),
        new("/admin/datasets/{datasetId:guid}/versions/{version:int}/cases", HttpMethod.Get, ids => $"/admin/datasets/{ids.DatasetId}/versions/1/cases"),
        new("/admin/prompts", HttpMethod.Get, ids => $"/admin/prompts?tenantId={ids.TenantId}", ByQuery: true),
        new("/admin/prompts", HttpMethod.Post, _ => "/admin/prompts", ids => new { tenantId = ids.TenantId, name = "intruder" }, ByQuery: true),
        new("/admin/prompts/{promptId:guid}", HttpMethod.Get, ids => $"/admin/prompts/{ids.PromptId}"),
        new("/admin/prompts/{promptId:guid}/versions", HttpMethod.Get, ids => $"/admin/prompts/{ids.PromptId}/versions"),
        new("/admin/prompts/{promptId:guid}/versions", HttpMethod.Post, ids => $"/admin/prompts/{ids.PromptId}/versions", _ => new { messages = new[] { new { role = "user", content = "{{text}}" } } }),
        new("/admin/prompts/{promptId:guid}/versions/{version:int}", HttpMethod.Get, ids => $"/admin/prompts/{ids.PromptId}/versions/1"),
    ];

    /// <summary>The route parameters that carry a tenant-scoped id; a route with one must be in <see cref="Routes"/> (the coverage gate).</summary>
    public static IReadOnlyList<string> TenantScopedParameters { get; } = ["tenantId", "keyId", "apiKeyId", "requestId", "datasetId", "promptId"];

    /// <summary>A JSONL Dataset Version upload body.</summary>
    public static HttpContent Jsonl(string text) => new StringContent(text, System.Text.Encoding.UTF8, "application/x-ndjson");

    /// <summary>
    /// Calls every route that has a child id with the caller's <em>own</em> Tenant in the path and a foreign child id; the child
    /// is looked up inside the path's Tenant, so the answer must be 404 and nothing may change.
    /// </summary>
    public static async Task AssertAllForeignChildIdsAreNotFoundAsync(AdminBrowser caller, Guid ownTenantId, Guid foreignApiKeyId)
    {
        ArgumentNullException.ThrowIfNull(caller);
        foreach (var route in Routes.Where(r => r.HasChildId))
        {
            using var response = await caller.SendJsonAsync(route.Method, route.Path(new ForeignIds(ownTenantId, foreignApiKeyId)), route.Body?.Invoke(new ForeignIds(ownTenantId, foreignApiKeyId)));
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{route.Name} must not reach another Tenant's child id");
            (await AdminBrowser.ErrorCodeAsync(response)).ShouldBe("not_found", route.Name);
        }
    }

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
            using var response = await outsider.SendJsonAsync(route.Method, route.Path(foreign), route.Body?.Invoke(foreign));
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{route.Name} must hide a Tenant outside the grant");
            (await AdminBrowser.ErrorCodeAsync(response)).ShouldBe("not_found", route.Name);
        }
    }
}
