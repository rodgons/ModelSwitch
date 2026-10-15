using Microsoft.Extensions.Time.Testing;
using ModelSwitch.Identity.Application;
using ModelSwitch.Identity.Domain;
using ModelSwitch.SharedKernel.Errors;
using Shouldly;

namespace ModelSwitch.UnitTests.Identity;

public sealed class ApiKeyAuthenticatorTests
{
    private static readonly byte[] _pepper = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly ApiKeyHasher _hasher = new(_pepper);
    private readonly FakeApiKeyStore _store = new();
    private readonly ApiKeyAuthenticator _authenticator;
    private readonly GeneratedApiKey _key = ApiKeyFormat.Generate();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _apiKeyId = Guid.NewGuid();

    public ApiKeyAuthenticatorTests()
    {
        _authenticator = new ApiKeyAuthenticator(_store, _hasher, new VerifiedKeyCache(_time), _time);
        Store();
    }

    [Fact]
    public async Task Valid_key_authenticates_and_carries_tenant_and_scopes()
    {
        var result = await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.TenantId.ShouldBe(_tenantId);
        result.Value.ApiKeyId.ShouldBe(_apiKeyId);
        result.Value.KeyPrefix.ShouldBe(_key.Prefix);
        result.Value.Scopes.ShouldBe(["inference", "models:read"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("msk_short")]
    [InlineData("not-a-key")]
    public async Task Missing_or_malformed_key_is_invalid_and_never_reaches_the_store(string? presented)
    {
        var result = await _authenticator.AuthenticateAsync(presented, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.InvalidApiKey);
        _store.Lookups.ShouldBe(0);
    }

    [Fact]
    public async Task Unknown_prefix_is_invalid()
    {
        var other = ApiKeyFormat.Generate();

        var result = await _authenticator.AuthenticateAsync(other.Plaintext, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.InvalidApiKey);
    }

    [Fact]
    public async Task Right_prefix_with_wrong_secret_is_invalid()
    {
        var forged = _key.Plaintext[..13] + new string('A', ApiKeyFormat.SecretLength);

        var result = await _authenticator.AuthenticateAsync(forged, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.InvalidApiKey);
    }

    [Fact]
    public async Task Revoked_key_is_invalid()
    {
        Store(revokedAt: _time.GetUtcNow().AddMinutes(-1));

        var result = await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.InvalidApiKey);
    }

    [Fact]
    public async Task Key_revoked_in_the_future_still_works_until_then()
    {
        Store(revokedAt: _time.GetUtcNow().AddMinutes(5));

        var result = await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Expired_key_is_invalid()
    {
        Store(expiresAt: _time.GetUtcNow().AddSeconds(-1));

        var result = await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.InvalidApiKey);
    }

    [Fact]
    public async Task Key_expiring_exactly_now_is_invalid()
    {
        Store(expiresAt: _time.GetUtcNow());

        var result = await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.InvalidApiKey);
    }

    [Fact]
    public async Task Key_that_has_not_expired_yet_is_valid()
    {
        Store(expiresAt: _time.GetUtcNow().AddDays(1));

        var result = await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Suspended_tenant_is_tenant_suspended_not_invalid_key()
    {
        Store(tenantStatus: TenantStatus.Suspended);

        var result = await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.TenantSuspended);
        result.Error.Code.HttpStatus.ShouldBe(403);
    }

    [Fact]
    public async Task Suspended_tenant_with_a_revoked_key_does_not_reveal_the_suspension()
    {
        Store(tenantStatus: TenantStatus.Suspended, revokedAt: _time.GetUtcNow().AddMinutes(-1));

        var result = await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.InvalidApiKey);
    }

    [Fact]
    public async Task Failures_do_not_say_why_the_key_is_invalid()
    {
        var unknown = await _authenticator.AuthenticateAsync(ApiKeyFormat.Generate().Plaintext, CancellationToken.None);
        Store(revokedAt: _time.GetUtcNow().AddMinutes(-1));
        var revoked = await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);

        revoked.Error.ShouldBe(unknown.Error);
    }

    [Fact]
    public async Task Verified_key_is_served_from_cache_within_30_seconds()
    {
        await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(29));

        var result = await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _store.Lookups.ShouldBe(1);
    }

    [Fact]
    public async Task Cache_entry_expires_after_30_seconds()
    {
        await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(30));

        await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);

        _store.Lookups.ShouldBe(2);
    }

    [Fact]
    public async Task Revocation_takes_effect_no_later_than_30_seconds_after_the_key_was_cached()
    {
        await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);
        Store(revokedAt: _time.GetUtcNow());

        _time.Advance(TimeSpan.FromSeconds(10));
        (await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        _time.Advance(TimeSpan.FromSeconds(20));
        var result = await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.InvalidApiKey);
    }

    [Fact]
    public async Task Suspending_a_tenant_takes_effect_after_the_cache_expires()
    {
        await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);
        Store(tenantStatus: TenantStatus.Suspended);
        _time.Advance(TimeSpan.FromSeconds(30));

        var result = await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.TenantSuspended);
    }

    [Fact]
    public async Task Cached_key_is_not_served_past_its_own_expiry()
    {
        Store(expiresAt: _time.GetUtcNow().AddSeconds(10));
        await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);

        _time.Advance(TimeSpan.FromSeconds(11));
        var result = await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.InvalidApiKey);
    }

    [Fact]
    public async Task Failures_are_not_cached()
    {
        Store(revokedAt: _time.GetUtcNow().AddMinutes(-1));
        await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);
        Store();

        var result = await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _store.Lookups.ShouldBe(2);
    }

    [Fact]
    public async Task Cache_is_keyed_by_the_whole_key_so_a_forged_secret_with_a_cached_prefix_is_rejected()
    {
        await _authenticator.AuthenticateAsync(_key.Plaintext, CancellationToken.None);
        var forged = _key.Plaintext[..13] + new string('B', ApiKeyFormat.SecretLength);

        var result = await _authenticator.AuthenticateAsync(forged, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.InvalidApiKey);
    }

    private void Store(DateTimeOffset? revokedAt = null, DateTimeOffset? expiresAt = null, TenantStatus tenantStatus = TenantStatus.Active)
        => _store.Record = new ApiKeyRecord(
            _apiKeyId,
            _tenantId,
            _key.Prefix,
            _hasher.Hash(_key.Plaintext),
            ["inference", "models:read"],
            expiresAt,
            revokedAt,
            tenantStatus);

    private sealed class FakeApiKeyStore : IApiKeyStore
    {
        public ApiKeyRecord? Record { get; set; }

        public int Lookups { get; private set; }

        public Task<ApiKeyRecord?> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken)
        {
            Lookups++;
            return Task.FromResult(Record is not null && Record.KeyPrefix == keyPrefix ? Record : null);
        }
    }
}
