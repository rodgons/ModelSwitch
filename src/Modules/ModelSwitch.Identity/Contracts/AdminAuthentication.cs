using System.Security.Claims;

namespace ModelSwitch.Identity.Contracts;

/// <summary>The Administrator the system sees: id, sign-in email, Role and Tenant Grant.</summary>
public sealed record AdministratorPrincipal(Guid Id, string Email, AdministratorRole Role, TenantGrant Grant);

/// <summary>Names shared by the Api host (cookie scheme, policies) and Identity (endpoints, current-administrator accessor).</summary>
public static class AdminAuthentication
{
    public const string Scheme = "Administrator";

    /// <summary>The <c>__Host-</c> prefix makes browsers insist on Secure, Path=/ and no Domain (security.md section 3).</summary>
    public const string CookieName = "__Host-ms_session";

    public const string AntiforgeryCookieName = "__Host-ms_xsrf";

    public const string AntiforgeryHeaderName = "X-XSRF-TOKEN";

    public const string AdminPathPrefix = "/admin";

    /// <summary>A session expires this long after its last use (ADR-0011, security.md section 3).</summary>
    public static readonly TimeSpan SlidingLifetime = TimeSpan.FromHours(8);

    /// <summary>A session expires this long after sign-in however busy it is.</summary>
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(24);
}

/// <summary>Endpoint policies by Role. Each one authenticates with the cookie scheme only.</summary>
public static class AdminPolicies
{
    public const string Viewer = "admin:viewer";
    public const string Operator = "admin:operator";
    public const string Owner = "admin:owner";

    public static string For(AdministratorRole required) => required switch
    {
        AdministratorRole.Viewer => Viewer,
        AdministratorRole.Operator => Operator,
        AdministratorRole.Owner => Owner,
        _ => throw new ArgumentOutOfRangeException(nameof(required)),
    };
}

/// <summary>The claims of an Administrator principal, rebuilt from the database on every request.</summary>
public static class AdminClaims
{
    public const string Role = "ms_role";
    public const string AllTenants = "ms_all_tenants";
    public const string Tenant = "ms_tenant";

    public static ClaimsPrincipal ToPrincipal(AdministratorPrincipal administrator)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, administrator.Id.ToString()),
            new(ClaimTypes.Email, administrator.Email),
            new(Role, AdministratorRoles.ToWire(administrator.Role)),
        };

        if (administrator.Grant.IsAll)
        {
            claims.Add(new Claim(AllTenants, "true"));
        }

        claims.AddRange(administrator.Grant.TenantIds.Select(id => new Claim(Tenant, id.ToString())));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AdminAuthentication.Scheme));
    }

    /// <summary>The Administrator in <paramref name="user"/>, or null when it was not authenticated by the Administrator scheme.</summary>
    public static AdministratorPrincipal? FromPrincipal(ClaimsPrincipal? user)
    {
        var identity = user?.Identities.FirstOrDefault(i => i.IsAuthenticated && i.AuthenticationType == AdminAuthentication.Scheme);
        if (identity is null
            || !Guid.TryParse(identity.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id)
            || !AdministratorRoles.TryParse(identity.FindFirst(Role)?.Value, out var role))
        {
            return null;
        }

        var grant = identity.HasClaim(AllTenants, "true")
            ? TenantGrant.AllTenants
            : TenantGrant.Only(identity.FindAll(Tenant).Select(c => Guid.TryParse(c.Value, out var tenantId) ? tenantId : Guid.Empty).Where(g => g != Guid.Empty));
        return new AdministratorPrincipal(id, identity.FindFirst(ClaimTypes.Email)?.Value ?? string.Empty, role, grant);
    }
}
