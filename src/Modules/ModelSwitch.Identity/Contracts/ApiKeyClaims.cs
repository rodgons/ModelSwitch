using System.Security.Claims;

namespace ModelSwitch.Identity.Contracts;

/// <summary>
/// The claims an authenticated API Key principal carries, and the authorization policies built on its Scopes.
/// The Tenant always comes from the key, never from a request field.
/// </summary>
public static class ApiKeyClaims
{
    public const string TenantId = "tenant_id";
    public const string ApiKeyId = "api_key_id";
    public const string KeyPrefix = "key_prefix";
    public const string Scope = "scope";

    /// <summary>Policy: the key carries the <c>inference</c> Scope (chat completions, model list).</summary>
    public const string InferencePolicy = "scope:inference";

    /// <summary>Policy: the key carries <c>models:read</c> or <c>inference</c> (the model list).</summary>
    public const string ModelsReadPolicy = "scope:models:read";

    /// <summary>The key's Tenant, or null when the principal is not an authenticated API Key.</summary>
    public static Guid? FindTenantId(ClaimsPrincipal? principal)
        => Guid.TryParse(principal?.FindFirst(TenantId)?.Value, out var id) ? id : null;

    /// <summary>The authenticated API Key's id, or null when the principal is not an authenticated API Key.</summary>
    public static Guid? FindApiKeyId(ClaimsPrincipal? principal)
        => Guid.TryParse(principal?.FindFirst(ApiKeyId)?.Value, out var id) ? id : null;
}
