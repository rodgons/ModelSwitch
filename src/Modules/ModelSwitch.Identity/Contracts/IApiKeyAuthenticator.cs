using ModelSwitch.SharedKernel;

namespace ModelSwitch.Identity.Contracts;

/// <summary>
/// Authenticates the secret presented on <c>Authorization: Bearer</c>. Failures are
/// <c>invalid_api_key</c> (missing, malformed, unknown, revoked or expired: indistinguishable on purpose)
/// or <c>tenant_suspended</c>.
/// </summary>
public interface IApiKeyAuthenticator
{
    Task<Result<AuthenticatedApiKey>> AuthenticateAsync(string? presentedKey, CancellationToken cancellationToken);
}
