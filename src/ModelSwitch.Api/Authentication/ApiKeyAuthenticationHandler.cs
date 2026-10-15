using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Api.Authentication;

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";

    public const string TenantIdClaim = "tenant_id";
    public const string ApiKeyIdClaim = "api_key_id";
    public const string KeyPrefixClaim = "key_prefix";
    public const string ScopeClaim = "scope";
}

/// <summary>
/// Transport only: reads the bearer token, asks <see cref="IApiKeyAuthenticator"/> and turns the answer into a
/// principal or a 401/403 Problem Details response. Every rule lives in Identity (architecture.md section 3).
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyAuthenticator authenticator)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string _bearerPrefix = "Bearer ";
    private const string _failureKey = "ModelSwitch.ApiKeyFailure";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (string.IsNullOrEmpty(header))
        {
            return AuthenticateResult.NoResult();
        }

        var token = header.StartsWith(_bearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? header[_bearerPrefix.Length..].Trim()
            : null;

        var result = await authenticator.AuthenticateAsync(token, Context.RequestAborted);
        if (result.IsFailure)
        {
            Context.Items[_failureKey] = result.Error;
            return AuthenticateResult.Fail(result.Error.Code.Value);
        }

        var key = result.Value;
        var identity = new ClaimsIdentity(
            [
                new Claim(ApiKeyDefaults.TenantIdClaim, key.TenantId.ToString()),
                new Claim(ApiKeyDefaults.ApiKeyIdClaim, key.ApiKeyId.ToString()),
                new Claim(ApiKeyDefaults.KeyPrefixClaim, key.KeyPrefix),
                .. key.Scopes.Select(scope => new Claim(ApiKeyDefaults.ScopeClaim, scope)),
            ],
            Scheme.Name);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var error = Context.Items.TryGetValue(_failureKey, out var stored) && stored is Error failure
            ? failure
            : new Error(ErrorCode.InvalidApiKey, "The API key is missing or invalid.");

        var status = error.Code.HttpStatus ?? StatusCodes.Status401Unauthorized;
        if (status == StatusCodes.Status401Unauthorized)
        {
            Response.Headers.WWWAuthenticate = "Bearer";
        }

        await ProblemFor(error, status).ExecuteAsync(Context);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        var error = new Error(ErrorCode.InsufficientScope, "The API key does not have the required scope.");
        return ProblemFor(error, StatusCodes.Status403Forbidden).ExecuteAsync(Context);
    }

    // RFC 9457 body plus the OpenAI `error` member (ADR-0012).
    private IResult ProblemFor(Error error, int status) => Results.Problem(
        statusCode: status,
        title: error.Message,
        detail: error.Message,
        type: $"/errors/{error.Code.Value}",
        instance: Request.Path,
        extensions: new Dictionary<string, object?>
        {
            ["errorCode"] = error.Code.Value,
            ["error"] = new Dictionary<string, object?>
            {
                ["message"] = error.Message,
                ["type"] = error.Code.Type,
                ["code"] = error.Code.Value,
                ["param"] = null,
            },
        });
}
