using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelSwitch.Api.Authentication;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using Shouldly;

namespace ModelSwitch.UnitTests.Api;

public sealed class ApiKeyAuthenticationHandlerTests
{
    private static readonly Guid _tenantId = Guid.Parse("0192f0c8-0000-7000-8000-000000000001");
    private static readonly Guid _keyId = Guid.Parse("0192f0c8-0000-7000-8000-000000000002");

    private readonly FakeAuthenticator _authenticator = new();

    [Fact]
    public async Task No_authorization_header_is_no_result_and_the_authenticator_is_not_called()
    {
        var (handler, _) = await CreateAsync(authorization: null);

        var result = await handler.AuthenticateAsync();

        result.None.ShouldBeTrue();
        _authenticator.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Bearer_token_is_passed_to_the_authenticator_and_claims_are_built()
    {
        _authenticator.Result = new AuthenticatedApiKey(_keyId, _tenantId, "7f3a9c21", ["inference", "models:read"]);
        var (handler, _) = await CreateAsync("bearer  msk_token ");

        var result = await handler.AuthenticateAsync();

        _authenticator.Presented.ShouldBe("msk_token");
        result.Succeeded.ShouldBeTrue();
        var principal = result.Principal!;
        principal.FindFirstValue(ApiKeyDefaults.TenantIdClaim).ShouldBe(_tenantId.ToString());
        principal.FindFirstValue(ApiKeyDefaults.ApiKeyIdClaim).ShouldBe(_keyId.ToString());
        principal.FindFirstValue(ApiKeyDefaults.KeyPrefixClaim).ShouldBe("7f3a9c21");
        principal.FindAll(ApiKeyDefaults.ScopeClaim).Select(c => c.Value).ShouldBe(["inference", "models:read"]);
    }

    [Fact]
    public async Task Non_bearer_scheme_is_treated_as_a_missing_key()
    {
        var (handler, _) = await CreateAsync("Basic abc");

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeFalse();
        _authenticator.Presented.ShouldBeNull();
        _authenticator.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Invalid_key_challenges_with_401_problem_details_and_the_openai_error_member()
    {
        _authenticator.Result = new Error(ErrorCode.InvalidApiKey, "The API key is missing or invalid.");
        var (handler, context) = await CreateAsync("Bearer msk_bad");

        await handler.AuthenticateAsync();
        await handler.ChallengeAsync(null);

        context.Response.StatusCode.ShouldBe(401);
        context.Response.Headers.WWWAuthenticate.ToString().ShouldBe("Bearer");
        context.Response.ContentType.ShouldStartWith("application/problem+json");
        var body = ReadBody(context);
        body.GetProperty("errorCode").GetString().ShouldBe("invalid_api_key");
        body.GetProperty("status").GetInt32().ShouldBe(401);
        body.GetProperty("error").GetProperty("code").GetString().ShouldBe("invalid_api_key");
        body.GetProperty("error").GetProperty("type").GetString().ShouldBe("authentication_error");
    }

    [Fact]
    public async Task Suspended_tenant_challenges_with_403()
    {
        _authenticator.Result = new Error(ErrorCode.TenantSuspended, "The tenant is suspended.");
        var (handler, context) = await CreateAsync("Bearer msk_ok");

        await handler.AuthenticateAsync();
        await handler.ChallengeAsync(null);

        context.Response.StatusCode.ShouldBe(403);
        context.Response.Headers.WWWAuthenticate.ToString().ShouldBeEmpty();
        ReadBody(context).GetProperty("errorCode").GetString().ShouldBe("tenant_suspended");
    }

    [Fact]
    public async Task Missing_header_challenge_is_invalid_api_key_401()
    {
        var (handler, context) = await CreateAsync(authorization: null);

        await handler.AuthenticateAsync();
        await handler.ChallengeAsync(null);

        context.Response.StatusCode.ShouldBe(401);
        ReadBody(context).GetProperty("errorCode").GetString().ShouldBe("invalid_api_key");
    }

    [Fact]
    public async Task Forbidden_is_insufficient_scope_403()
    {
        var (handler, context) = await CreateAsync("Bearer msk_ok");

        await handler.ForbidAsync(null);

        context.Response.StatusCode.ShouldBe(403);
        ReadBody(context).GetProperty("errorCode").GetString().ShouldBe("insufficient_scope");
    }

    private async Task<(IAuthenticationHandler Handler, DefaultHttpContext Context)> CreateAsync(string? authorization)
    {
        var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        context.Request.Path = "/v1/chat/completions";
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        var handler = new ApiKeyAuthenticationHandler(
            new StaticOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            _authenticator);
        await handler.InitializeAsync(new AuthenticationScheme(ApiKeyDefaults.Scheme, null, typeof(ApiKeyAuthenticationHandler)), context);
        return (handler, context);
    }

    private static JsonElement ReadBody(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return JsonDocument.Parse(context.Response.Body).RootElement.Clone();
    }

    private sealed class FakeAuthenticator : IApiKeyAuthenticator
    {
        public Result<AuthenticatedApiKey> Result { get; set; } = new Error(ErrorCode.InvalidApiKey, "The API key is missing or invalid.");

        public string? Presented { get; private set; }

        public int Calls { get; private set; }

        public Task<Result<AuthenticatedApiKey>> AuthenticateAsync(string? presentedKey, CancellationToken cancellationToken)
        {
            Calls++;
            Presented = presentedKey;
            return Task.FromResult(Result);
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
