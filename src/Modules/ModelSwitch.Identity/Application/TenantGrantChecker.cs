using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Identity.Application;

internal sealed class TenantGrantChecker(ICurrentAdministrator current) : ITenantGrantChecker
{
    public Result CheckAccess(Guid tenantId)
        => current.Current is { } administrator
            ? CheckAccess(administrator, tenantId)
            : Result.Failure(new Error(ErrorCode.Unauthenticated, "Sign in to continue."));

    public Result CheckAccess(AdministratorPrincipal administrator, Guid tenantId)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        return administrator.Grant.Allows(tenantId)
            ? Result.Success()
            : Result.Failure(new Error(ErrorCode.NotFound, "The resource was not found."));
    }
}
