using ModelSwitch.SharedKernel;

namespace ModelSwitch.Identity.Contracts;

/// <summary>The Administrator signed in on the current HTTP request. Other modules' admin endpoints and services use this, never the claims.</summary>
public interface ICurrentAdministrator
{
    /// <summary>Null when the request carries no valid Administrator session.</summary>
    AdministratorPrincipal? Current { get; }
}

/// <summary>
/// Tenant Grant enforcement for admin queries and commands that take a Tenant id (security.md section 3). A Tenant outside
/// the grant answers <c>not_found</c> (404), never 403, so its existence is not revealed.
/// </summary>
public interface ITenantGrantChecker
{
    /// <summary>Checks the current Administrator. Fails with <c>unauthenticated</c> when nobody is signed in.</summary>
    Result CheckAccess(Guid tenantId);

    Result CheckAccess(AdministratorPrincipal administrator, Guid tenantId);
}
