using System.Data.Common;
using Microsoft.Extensions.Time.Testing;
using ModelSwitch.Identity.Application;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Identity.Domain;
using ModelSwitch.SharedKernel.Errors;
using Shouldly;

namespace ModelSwitch.UnitTests.Identity;

internal sealed class FakeTenantAdminStore : ITenantAdminStore
{
    public Dictionary<Guid, Tenant> Tenants { get; } = [];

    public Dictionary<Guid, TenantLimitsRow> Limits { get; } = [];

    public List<AuditEntry> Audits { get; } = [];

    public Tenant AddTenant(string name, TenantStatus status = TenantStatus.Active, DateTimeOffset? createdAt = null)
    {
        var tenant = new Tenant { Id = Guid.CreateVersion7(), Name = name, Status = status, CreatedAt = createdAt ?? DateTimeOffset.UnixEpoch };
        Tenants[tenant.Id] = tenant;
        return tenant;
    }

    public Task<IReadOnlyList<Tenant>> ListAsync(TenantGrant grant, int limit, (DateTimeOffset CreatedAt, Guid Id)? after, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<Tenant>>([.. Tenants.Values
            .Where(t => grant.Allows(t.Id) && (after is not { } a || t.CreatedAt < a.CreatedAt || (t.CreatedAt == a.CreatedAt && t.Id.CompareTo(a.Id) < 0)))
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
            .Take(limit + 1)]);

    public Task<Tenant?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult(Tenants.TryGetValue(tenantId, out var tenant) ? Copy(tenant) : null);

    public Task<TenantLimitsRow?> FindLimitsAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult(Limits.GetValueOrDefault(tenantId));

    public Task<bool> AddAsync(Tenant tenant, AuditEntry audit, CancellationToken cancellationToken)
    {
        if (Tenants.Values.Any(t => t.Name == tenant.Name))
        {
            return Task.FromResult(false);
        }

        Tenants[tenant.Id] = tenant;
        Audits.Add(audit);
        return Task.FromResult(true);
    }

    public Task<bool> SaveAsync(Tenant tenant, AuditEntry audit, CancellationToken cancellationToken)
    {
        if (Tenants.Values.Any(t => t.Id != tenant.Id && t.Name == tenant.Name))
        {
            return Task.FromResult(false);
        }

        Tenants[tenant.Id] = tenant;
        Audits.Add(audit);
        return Task.FromResult(true);
    }

    public Task SaveLimitsAsync(TenantLimitsChange change, AuditEntry audit, CancellationToken cancellationToken)
    {
        var row = Limits.GetValueOrDefault(change.TenantId) ?? new TenantLimitsRow { TenantId = change.TenantId };
        row.RequestsPerMinute = change.RequestsPerMinute;
        row.MaxConcurrent = change.MaxConcurrent;
        row.MaxRequestCost = change.MaxRequestCost;
        Limits[change.TenantId] = row;
        Audits.Add(audit);
        return Task.CompletedTask;
    }

    public Task SaveAllowedAliasesAsync(Guid tenantId, string[] aliases, DateTimeOffset now, AuditEntry audit, CancellationToken cancellationToken)
    {
        var row = Limits.GetValueOrDefault(tenantId) ?? new TenantLimitsRow { TenantId = tenantId };
        row.AllowedAliases = aliases;
        Limits[tenantId] = row;
        Audits.Add(audit);
        return Task.CompletedTask;
    }

    private static Tenant Copy(Tenant t) => new() { Id = t.Id, Name = t.Name, Status = t.Status, CreatedAt = t.CreatedAt, UpdatedAt = t.UpdatedAt };
}

internal sealed class FakeApiKeyAdminStore : IApiKeyAdminStore
{
    public Dictionary<Guid, ApiKey> Keys { get; } = [];

    public List<AuditEntry> Audits { get; } = [];

    public Task<IReadOnlyList<ApiKey>> ListAsync(Guid tenantId, int limit, (DateTimeOffset CreatedAt, Guid Id)? after, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ApiKey>>([.. Keys.Values
            .Where(k => k.TenantId == tenantId && (after is not { } a || k.CreatedAt < a.CreatedAt || (k.CreatedAt == a.CreatedAt && k.Id.CompareTo(a.Id) < 0)))
            .OrderByDescending(k => k.CreatedAt).ThenByDescending(k => k.Id)
            .Take(limit + 1)]);

    public Task<ApiKey?> FindAsync(Guid tenantId, Guid keyId, CancellationToken cancellationToken)
        => Task.FromResult(Keys.Values.FirstOrDefault(k => k.TenantId == tenantId && k.Id == keyId));

    public Task<bool> AddAsync(ApiKey key, AuditEntry audit, CancellationToken cancellationToken)
    {
        Keys[key.Id] = key;
        Audits.Add(audit);
        return Task.FromResult(true);
    }

    public Task<bool> RevokeAsync(Guid tenantId, Guid keyId, DateTimeOffset at, AuditEntry audit, CancellationToken cancellationToken)
    {
        Keys[keyId].RevokedAt = at;
        Audits.Add(audit);
        return Task.FromResult(true);
    }
}

internal sealed class FakeBudgets : ITenantBudgets
{
    public Dictionary<Guid, decimal> Limits { get; } = [];

    public Task<decimal?> GetMonthlyLimitAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult<decimal?>(Limits.TryGetValue(tenantId, out var limit) ? limit : null);

    public Task SetMonthlyLimitAsync(Guid tenantId, decimal? amount, DbTransaction transaction, CancellationToken cancellationToken) => Task.CompletedTask;

    public Dictionary<Guid, TenantBudgetPeriod> Periods { get; } = [];

    public Task<TenantBudgetPeriod> GetCurrentPeriodAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult(Periods.TryGetValue(tenantId, out var period) ? period : new TenantBudgetPeriod(new DateOnly(2026, 10, 1), null, 0m, 0m));
}

public sealed class TenantServiceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeTenantAdminStore _store = new();
    private readonly FakeBudgets _budgets = new();
    private readonly VerifiedKeyCache _cache;

    public TenantServiceTests() => _cache = new VerifiedKeyCache(_time);

    private static AdministratorPrincipal Operator(TenantGrant grant) => new(Guid.CreateVersion7(), "op@example.com", AdministratorRole.Operator, grant);

    private TenantService Service(AdministratorPrincipal? administrator = null)
    {
        var current = new FixedCurrentAdministrator(administrator ?? Operator(TenantGrant.AllTenants));
        return new TenantService(_store, _budgets, current, new TenantGrantChecker(current), _cache, new VersionSevenGuids(), _time);
    }

    [Fact]
    public async Task Creating_a_tenant_trims_the_name_audits_it_and_starts_it_active()
    {
        var result = await Service().CreateAsync(new CreateTenantRequest("  Acme  "), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Name.ShouldBe("Acme");
        result.Value.Status.ShouldBe("active");
        var audit = _store.Audits.ShouldHaveSingleItem();
        audit.Action.ShouldBe(AuditActions.TenantCreated);
        audit.TenantId.ShouldBe(result.Value.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task A_blank_tenant_name_is_a_validation_error(string? name)
    {
        var result = await Service().CreateAsync(new CreateTenantRequest(name), CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.ValidationFailed);
        result.Error.FieldErrors.ShouldNotBeNull().ShouldContainKey("name");
        _store.Audits.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_name_over_the_limit_or_with_control_characters_is_rejected()
    {
        (await Service().CreateAsync(new CreateTenantRequest(new string('a', TenantRules.MaxNameLength + 1)), CancellationToken.None)).IsFailure.ShouldBeTrue();
        (await Service().CreateAsync(new CreateTenantRequest("bad\nname"), CancellationToken.None)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task A_duplicate_name_is_a_conflict()
    {
        _store.AddTenant("Acme");

        var result = await Service().CreateAsync(new CreateTenantRequest("Acme"), CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.Conflict);
    }

    [Fact]
    public async Task An_operator_with_an_explicit_grant_cannot_create_a_tenant()
    {
        var result = await Service(Operator(TenantGrant.Only([Guid.CreateVersion7()]))).CreateAsync(new CreateTenantRequest("Acme"), CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.Forbidden);
        _store.Tenants.ShouldBeEmpty();
    }

    [Fact]
    public async Task Listing_shows_only_granted_tenants_newest_first_with_a_cursor()
    {
        var oldest = _store.AddTenant("a", createdAt: _time.GetUtcNow().AddMinutes(-3));
        var middle = _store.AddTenant("b", createdAt: _time.GetUtcNow().AddMinutes(-2));
        var newest = _store.AddTenant("c", createdAt: _time.GetUtcNow().AddMinutes(-1));
        _store.AddTenant("hidden", createdAt: _time.GetUtcNow());
        var service = Service(Operator(TenantGrant.Only([oldest.Id, middle.Id, newest.Id])));

        var first = await service.ListAsync("2", null, CancellationToken.None);
        var second = await service.ListAsync("2", first.Value.NextCursor, CancellationToken.None);

        first.Value.Items.Select(t => t.Name).ShouldBe(["c", "b"]);
        first.Value.NextCursor.ShouldNotBeNull();
        second.Value.Items.Select(t => t.Name).ShouldBe(["a"]);
        second.Value.NextCursor.ShouldBeNull();
    }

    [Theory]
    [InlineData("0", null)]
    [InlineData("201", null)]
    [InlineData("abc", null)]
    [InlineData(null, "not-a-cursor")]
    public async Task An_invalid_limit_or_cursor_is_a_validation_error(string? limit, string? cursor)
    {
        var result = await Service().ListAsync(limit, cursor, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.ValidationFailed);
    }

    [Fact]
    public async Task A_tenant_outside_the_grant_is_not_found_for_every_operation()
    {
        var foreign = _store.AddTenant("foreign");
        var service = Service(Operator(TenantGrant.Only([Guid.CreateVersion7()])));

        (await service.GetAsync(foreign.Id, CancellationToken.None)).Error.Code.ShouldBe(ErrorCode.NotFound);
        (await service.UpdateAsync(foreign.Id, new UpdateTenantRequest("x", null), CancellationToken.None)).Error.Code.ShouldBe(ErrorCode.NotFound);
        (await service.SetLimitsAsync(foreign.Id, new UpdateLimitsRequest(1, 1, null, null), CancellationToken.None)).Error.Code.ShouldBe(ErrorCode.NotFound);
        (await service.SetModelAccessAsync(foreign.Id, new UpdateModelAccessRequest(["*"]), CancellationToken.None)).Error.Code.ShouldBe(ErrorCode.NotFound);
        _store.Audits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Suspending_audits_a_suspension_and_drops_cached_keys()
    {
        var tenant = _store.AddTenant("Acme");
        _cache.Set([1, 2, 3], new AuthenticatedApiKey(Guid.CreateVersion7(), tenant.Id, "abcdefgh", [ApiKeyScopes.Inference]), null);

        var result = await Service().UpdateAsync(tenant.Id, new UpdateTenantRequest(null, "suspended"), CancellationToken.None);

        result.Value.Status.ShouldBe("suspended");
        _store.Audits.ShouldHaveSingleItem().Action.ShouldBe(AuditActions.TenantSuspended);
        _cache.TryGet([1, 2, 3], out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Reactivating_and_renaming_use_their_own_actions()
    {
        var tenant = _store.AddTenant("Acme", TenantStatus.Suspended);
        await Service().UpdateAsync(tenant.Id, new UpdateTenantRequest(null, "active"), CancellationToken.None);
        await Service().UpdateAsync(tenant.Id, new UpdateTenantRequest("Acme 2", null), CancellationToken.None);

        _store.Audits.Select(a => a.Action).ShouldBe([AuditActions.TenantReactivated, AuditActions.TenantModified]);
    }

    [Fact]
    public async Task An_update_that_changes_nothing_writes_nothing()
    {
        var tenant = _store.AddTenant("Acme");

        var result = await Service().UpdateAsync(tenant.Id, new UpdateTenantRequest("Acme", "active"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _store.Audits.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_status_is_a_validation_error()
    {
        var tenant = _store.AddTenant("Acme");

        var result = await Service().UpdateAsync(tenant.Id, new UpdateTenantRequest(null, "deleted"), CancellationToken.None);

        result.Error.FieldErrors.ShouldNotBeNull().ShouldContainKey("status");
    }

    [Fact]
    public async Task Setting_limits_replaces_them_and_audits_before_and_after()
    {
        var tenant = _store.AddTenant("Acme");
        _store.Limits[tenant.Id] = new TenantLimitsRow { TenantId = tenant.Id, RequestsPerMinute = 5 };

        var result = await Service().SetLimitsAsync(tenant.Id, new UpdateLimitsRequest(60, null, 0.5m, new BudgetRequest(100m, "month")), CancellationToken.None);

        result.Value.RequestsPerMinute.ShouldBe(60);
        result.Value.MaxConcurrent.ShouldBeNull();
        result.Value.Budget.ShouldBe(new BudgetView(100m, "month", "USD"));
        var audit = _store.Audits.ShouldHaveSingleItem();
        audit.Action.ShouldBe(AuditActions.TenantLimitsChanged);
        audit.Before!["requestsPerMinute"].ShouldBe(5);
        audit.After!["requestsPerMinute"].ShouldBe(60);
    }

    [Theory]
    [InlineData(0, null, null, "requestsPerMinute")]
    [InlineData(null, -1, null, "maxConcurrent")]
    [InlineData(null, 10_000_001, null, "maxConcurrent")]
    [InlineData(null, null, -0.01, "maxRequestCost")]
    public async Task Out_of_range_limits_are_validation_errors(int? rpm, int? concurrent, double? cost, string field)
    {
        var tenant = _store.AddTenant("Acme");

        var result = await Service().SetLimitsAsync(tenant.Id, new UpdateLimitsRequest(rpm, concurrent, (decimal?)cost, null), CancellationToken.None);

        result.Error.FieldErrors.ShouldNotBeNull().ShouldContainKey(field);
        _store.Audits.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null, "month", "budget.amount")]
    [InlineData(-1.0, "month", "budget.amount")]
    [InlineData(10.0, "week", "budget.period")]
    public async Task A_bad_budget_is_a_validation_error(double? amount, string period, string field)
    {
        var tenant = _store.AddTenant("Acme");

        var result = await Service().SetLimitsAsync(tenant.Id, new UpdateLimitsRequest(null, null, null, new BudgetRequest((decimal?)amount, period)), CancellationToken.None);

        result.Error.FieldErrors.ShouldNotBeNull().ShouldContainKey(field);
    }

    [Fact]
    public async Task A_null_budget_removes_it()
    {
        var tenant = _store.AddTenant("Acme");
        _budgets.Limits[tenant.Id] = 5m;

        var result = await Service().SetLimitsAsync(tenant.Id, new UpdateLimitsRequest(null, null, null, null), CancellationToken.None);

        result.Value.Budget.ShouldBeNull();
        _store.Audits.Single().Before!["budgetMonthly"].ShouldBe(5m);
    }

    [Fact]
    public async Task Model_access_is_deduplicated_and_sorted()
    {
        var tenant = _store.AddTenant("Acme");

        var result = await Service().SetModelAccessAsync(tenant.Id, new UpdateModelAccessRequest(["fast", "balanced", "fast"]), CancellationToken.None);

        result.Value.AllowedAliases.ShouldBe(["balanced", "fast"]);
        _store.Limits[tenant.Id].AllowedAliases.ShouldBe(["balanced", "fast"]);
        _store.Audits.ShouldHaveSingleItem().Action.ShouldBe(AuditActions.TenantModelAccessChanged);
    }

    [Fact]
    public async Task Invalid_model_access_is_a_validation_error()
    {
        var tenant = _store.AddTenant("Acme");
        string?[]?[] cases = [null, [], ["*", "fast"], ["Has Space"], [""], [null]];

        foreach (var aliases in cases)
        {
            var result = await Service().SetModelAccessAsync(tenant.Id, new UpdateModelAccessRequest(aliases), CancellationToken.None);

            result.Error.FieldErrors.ShouldNotBeNull().ShouldContainKey("allowedAliases");
        }

        _store.Audits.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_detail_defaults_to_unlimited_and_every_alias()
    {
        var tenant = _store.AddTenant("Acme");

        var result = await Service().GetAsync(tenant.Id, CancellationToken.None);

        result.Value.Limits.ShouldBe(new LimitsView(null, null, null, null));
        result.Value.ModelAccess.AllowedAliases.ShouldBe(["*"]);
    }

    [Fact]
    public async Task The_detail_shows_the_current_budget_period_with_its_spent_and_reserved_amounts()
    {
        var tenant = _store.AddTenant("Acme");
        _budgets.Periods[tenant.Id] = new TenantBudgetPeriod(new DateOnly(2026, 10, 1), 10m, 0.25m, 1.5m);

        var result = await Service().GetAsync(tenant.Id, CancellationToken.None);

        result.Value.BudgetPeriod.ShouldBe(new BudgetPeriodView(new DateOnly(2026, 10, 1), 10m, 0.25m, 1.5m, "USD"));
    }
}

public sealed class ApiKeyAdminServiceTests
{
    private static readonly byte[] _pepper = new byte[32];
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeTenantAdminStore _tenants = new();
    private readonly FakeApiKeyAdminStore _keys = new();
    private readonly VerifiedKeyCache _cache;
    private readonly ApiKeyHasher _hasher = new(_pepper);

    public ApiKeyAdminServiceTests() => _cache = new VerifiedKeyCache(_time);

    private ApiKeyAdminService Service(AdministratorPrincipal? administrator = null)
    {
        var current = new FixedCurrentAdministrator(administrator ?? new AdministratorPrincipal(Guid.CreateVersion7(), "op@example.com", AdministratorRole.Operator, TenantGrant.AllTenants));
        return new ApiKeyAdminService(_keys, _tenants, current, new TenantGrantChecker(current), _hasher, _cache, new VersionSevenGuids(), _time);
    }

    [Fact]
    public async Task A_created_key_is_returned_once_stored_only_as_a_hash_and_audited_without_the_secret()
    {
        var tenant = _tenants.AddTenant("Acme");

        var result = await Service().CreateAsync(tenant.Id, new CreateApiKeyRequest("ci", null, null), CancellationToken.None);

        var created = result.Value;
        ApiKeyFormat.TryParse(created.Key, out var parsed).ShouldBeTrue();
        created.KeyPrefix.ShouldBe($"msk_{parsed.Prefix}");
        created.Scopes.ShouldBe([ApiKeyScopes.Inference]);
        var stored = _keys.Keys.Values.Single();
        stored.KeyHash.ShouldBe(_hasher.Hash(created.Key));
        var audit = _keys.Audits.ShouldHaveSingleItem();
        audit.Action.ShouldBe(AuditActions.ApiKeyCreated);
        audit.TenantId.ShouldBe(tenant.Id);
        System.Text.Json.JsonSerializer.Serialize(audit).ShouldNotContain(created.Key);
    }

    [Fact]
    public async Task Scopes_are_deduplicated_and_must_be_known()
    {
        var tenant = _tenants.AddTenant("Acme");

        var ok = await Service().CreateAsync(tenant.Id, new CreateApiKeyRequest("a", [ApiKeyScopes.ModelsRead, ApiKeyScopes.ModelsRead], null), CancellationToken.None);
        var bad = await Service().CreateAsync(tenant.Id, new CreateApiKeyRequest("b", ["admin"], null), CancellationToken.None);
        var empty = await Service().CreateAsync(tenant.Id, new CreateApiKeyRequest("c", [], null), CancellationToken.None);

        ok.Value.Scopes.ShouldBe([ApiKeyScopes.ModelsRead]);
        bad.Error.FieldErrors.ShouldNotBeNull().ShouldContainKey("scopes");
        empty.Error.FieldErrors.ShouldNotBeNull().ShouldContainKey("scopes");
    }

    [Fact]
    public async Task A_name_is_required_and_an_expiry_must_be_in_the_future()
    {
        var tenant = _tenants.AddTenant("Acme");

        var result = await Service().CreateAsync(tenant.Id, new CreateApiKeyRequest(" ", null, _time.GetUtcNow()), CancellationToken.None);

        result.Error.FieldErrors.ShouldNotBeNull().Keys.ShouldBe(["name", "expiresAt"], ignoreOrder: true);
        _keys.Keys.ShouldBeEmpty();
    }

    [Fact]
    public async Task Revoking_sets_revoked_at_audits_and_drops_cached_keys()
    {
        var tenant = _tenants.AddTenant("Acme");
        var created = (await Service().CreateAsync(tenant.Id, new CreateApiKeyRequest("ci", null, null), CancellationToken.None)).Value;
        _cache.Set([9], new AuthenticatedApiKey(created.Id, tenant.Id, "abcdefgh", [ApiKeyScopes.Inference]), null);
        _time.Advance(TimeSpan.FromMinutes(1));

        var result = await Service().RevokeAsync(tenant.Id, created.Id, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _keys.Keys[created.Id].RevokedAt.ShouldBe(_time.GetUtcNow());
        _keys.Audits.Last().Action.ShouldBe(AuditActions.ApiKeyRevoked);
        _cache.TryGet([9], out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Revoking_twice_is_idempotent_and_audits_once()
    {
        var tenant = _tenants.AddTenant("Acme");
        var created = (await Service().CreateAsync(tenant.Id, new CreateApiKeyRequest("ci", null, null), CancellationToken.None)).Value;

        await Service().RevokeAsync(tenant.Id, created.Id, CancellationToken.None);
        var again = await Service().RevokeAsync(tenant.Id, created.Id, CancellationToken.None);

        again.IsSuccess.ShouldBeTrue();
        _keys.Audits.Count(a => a.Action == AuditActions.ApiKeyRevoked).ShouldBe(1);
    }

    [Fact]
    public async Task A_key_of_another_tenant_is_not_found_even_with_a_grant_to_both_ids()
    {
        var mine = _tenants.AddTenant("mine");
        var other = _tenants.AddTenant("other");
        var created = (await Service().CreateAsync(other.Id, new CreateApiKeyRequest("ci", null, null), CancellationToken.None)).Value;

        var result = await Service().RevokeAsync(mine.Id, created.Id, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.NotFound);
        _keys.Keys[created.Id].RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_tenant_outside_the_grant_is_not_found_for_every_operation()
    {
        var foreign = _tenants.AddTenant("foreign");
        var service = Service(new AdministratorPrincipal(Guid.CreateVersion7(), "op@example.com", AdministratorRole.Operator, TenantGrant.None));

        (await service.ListAsync(foreign.Id, null, null, CancellationToken.None)).Error.Code.ShouldBe(ErrorCode.NotFound);
        (await service.CreateAsync(foreign.Id, new CreateApiKeyRequest("x", null, null), CancellationToken.None)).Error.Code.ShouldBe(ErrorCode.NotFound);
        (await service.RevokeAsync(foreign.Id, Guid.CreateVersion7(), CancellationToken.None)).Error.Code.ShouldBe(ErrorCode.NotFound);
        _keys.Keys.ShouldBeEmpty();
    }

    [Fact]
    public async Task Listing_pages_newest_first_and_never_exposes_the_hash()
    {
        var tenant = _tenants.AddTenant("Acme");
        for (var i = 0; i < 3; i++)
        {
            await Service().CreateAsync(tenant.Id, new CreateApiKeyRequest($"k{i}", null, null), CancellationToken.None);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        var first = await Service().ListAsync(tenant.Id, "2", null, CancellationToken.None);
        var second = await Service().ListAsync(tenant.Id, "2", first.Value.NextCursor, CancellationToken.None);

        first.Value.Items.Select(k => k.Name).ShouldBe(["k2", "k1"]);
        second.Value.Items.Select(k => k.Name).ShouldBe(["k0"]);
        second.Value.NextCursor.ShouldBeNull();
        typeof(ApiKeyView).GetProperties().Select(p => p.Name).ShouldNotContain("KeyHash");
    }
}
