using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Time.Testing;
using ModelSwitch.Identity.Application;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Identity.Domain;
using ModelSwitch.SharedKernel.Errors;
using Shouldly;

namespace ModelSwitch.UnitTests.Identity;

public sealed class AdministratorSignInServiceTests
{
    private const string _password = "correct horse battery";
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeAdministratorStore _store = new();
    private readonly RecordingAuditWriter _audit = new();

    private AdministratorSignInService Service() => new(_store, new PasswordHasher<Administrator>(), _audit, _time);

    [Fact]
    public async Task Valid_credentials_sign_in_and_audit_with_the_administrator_as_actor()
    {
        var owner = _store.Add("owner@example.com", AdministratorRole.Owner);

        var result = await Service().SignInAsync("  Owner@Example.com ", _password, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Id.ShouldBe(owner.Id);
        result.Value.Role.ShouldBe(AdministratorRole.Owner);
        result.Value.Grant.IsAll.ShouldBeTrue();
        _store.Audits.ShouldHaveSingleItem().Action.ShouldBe(AuditActions.AdministratorSignedIn);
        _store.Audits[0].ActorAdministratorId.ShouldBe(owner.Id);
        owner.LastSignedInAt.ShouldBe(_time.GetUtcNow());
    }

    [Theory]
    [InlineData(null, "x")]
    [InlineData("a@example.com", null)]
    [InlineData("  ", "")]
    public async Task A_missing_field_is_a_validation_error(string? email, string? password)
    {
        var result = await Service().SignInAsync(email, password, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.ValidationFailed);
        result.Error.FieldErrors.ShouldNotBeNull().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_account_give_the_same_answer()
    {
        _store.Add("owner@example.com", AdministratorRole.Owner);

        var wrong = await Service().SignInAsync("owner@example.com", "not the password!!", CancellationToken.None);
        var unknown = await Service().SignInAsync("nobody@example.com", _password, CancellationToken.None);

        wrong.Error.Code.ShouldBe(ErrorCode.Unauthenticated);
        unknown.Error.Code.ShouldBe(ErrorCode.Unauthenticated);
        wrong.Error.Message.ShouldBe(unknown.Error.Message);
    }

    [Fact]
    public async Task An_unknown_account_is_audited_without_an_actor_and_without_the_typed_email()
    {
        await Service().SignInAsync("nobody@example.com", _password, CancellationToken.None);

        var entry = _audit.Entries.ShouldHaveSingleItem();
        entry.Action.ShouldBe(AuditActions.AdministratorSignInFailed);
        entry.ActorAdministratorId.ShouldBeNull();
        entry.Details!["reason"].ShouldBe("unknown_account");
        entry.TargetId.ShouldBeNull();
    }

    [Fact]
    public async Task The_fifth_wrong_password_locks_the_account_and_the_right_password_is_then_refused()
    {
        var owner = _store.Add("owner@example.com", AdministratorRole.Owner);
        var service = Service();

        for (var i = 0; i < 5; i++)
        {
            (await service.SignInAsync("owner@example.com", "wrong password!!", CancellationToken.None)).IsFailure.ShouldBeTrue();
        }

        owner.IsLockedOut(_time.GetUtcNow()).ShouldBeTrue();
        var whileLocked = await service.SignInAsync("owner@example.com", _password, CancellationToken.None);
        whileLocked.Error.Code.ShouldBe(ErrorCode.Unauthenticated);
        _audit.Entries.Last().Details!["reason"].ShouldBe("locked_out");

        _time.Advance(TimeSpan.FromMinutes(15));
        (await service.SignInAsync("owner@example.com", _password, CancellationToken.None)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_deactivated_administrator_cannot_sign_in_even_with_the_right_password()
    {
        _store.Add("gone@example.com", AdministratorRole.Operator, active: false);

        var result = await Service().SignInAsync("gone@example.com", _password, CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.Unauthenticated);
        _audit.Entries.ShouldHaveSingleItem().Details!["reason"].ShouldBe("inactive");
    }

    [Fact]
    public async Task A_failed_sign_in_audit_never_contains_the_password()
    {
        _store.Add("owner@example.com", AdministratorRole.Owner);

        await Service().SignInAsync("owner@example.com", "typed-secret-value", CancellationToken.None);

        var shaped = AuditRecordFactory.ShapeDetails(_store.Audits.Single());
        shaped.ShouldNotContain("typed-secret-value");
    }
}

public sealed class AdministratorServiceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeAdministratorStore _store = new();
    private readonly Guid _tenantA = Guid.CreateVersion7();
    private readonly Guid _tenantB = Guid.CreateVersion7();

    public AdministratorServiceTests()
    {
        _store.KnownTenants.Add(_tenantA);
        _store.KnownTenants.Add(_tenantB);
    }

    private AdministratorService Service() => new(_store, new PasswordHasher<Administrator>(), new VersionSevenGuids(), _time);

    [Fact]
    public async Task Create_stores_a_hashed_password_normalised_email_and_an_audit_without_secrets()
    {
        var result = await Service().CreateAsync(
            new CreateAdministratorRequest(" Op@Example.com ", "a-long-enough-password", "operator", null, [_tenantA]),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var stored = _store.Administrators.Values.Single();
        stored.Email.ShouldBe("op@example.com");
        stored.PasswordHash.ShouldNotContain("a-long-enough-password");
        stored.Grants.Select(g => g.TenantId).ShouldBe([_tenantA]);
        var audit = _store.Audits.ShouldHaveSingleItem();
        audit.Action.ShouldBe(AuditActions.AdministratorCreated);
        AuditRecordFactory.ShapeDetails(audit).ShouldNotContain("a-long-enough-password");
    }

    [Fact]
    public async Task Create_reports_every_invalid_field()
    {
        var result = await Service().CreateAsync(new CreateAdministratorRequest("not-an-email", "short", "boss", null, null), CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.ValidationFailed);
        result.Error.FieldErrors!.Keys.ShouldBe(["email", "password", "role"], ignoreOrder: true);
    }

    [Fact]
    public async Task Create_rejects_unknown_tenants_in_the_grant()
    {
        var result = await Service().CreateAsync(
            new CreateAdministratorRequest("op@example.com", "a-long-enough-password", "viewer", null, [Guid.CreateVersion7()]),
            CancellationToken.None);

        result.Error.FieldErrors!.ShouldContainKey("tenantIds");
    }

    [Fact]
    public async Task An_owner_always_gets_the_all_tenants_grant()
    {
        var result = await Service().CreateAsync(
            new CreateAdministratorRequest("owner2@example.com", "a-long-enough-password", "owner", false, [_tenantA]),
            CancellationToken.None);

        result.Value.AllTenants.ShouldBeTrue();
        result.Value.TenantIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task All_tenants_together_with_a_tenant_list_is_contradictory()
    {
        var result = await Service().CreateAsync(
            new CreateAdministratorRequest("op@example.com", "a-long-enough-password", "operator", true, [_tenantA]),
            CancellationToken.None);

        result.Error.FieldErrors!.ShouldContainKey("tenantIds");
    }

    [Fact]
    public async Task A_duplicate_email_is_a_conflict()
    {
        _store.Add("op@example.com", AdministratorRole.Operator);

        var result = await Service().CreateAsync(
            new CreateAdministratorRequest("OP@example.com", "a-long-enough-password", "viewer", null, null),
            CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.Conflict);
    }

    [Fact]
    public async Task Update_audits_only_the_changed_fields_with_before_and_after()
    {
        var viewer = _store.Add("v@example.com", AdministratorRole.Viewer, tenants: [_tenantA]);

        var result = await Service().UpdateAsync(viewer.Id, new UpdateAdministratorRequest("operator", null, null, null, null), CancellationToken.None);

        result.Value.Role.ShouldBe("operator");
        result.Value.TenantIds.ShouldBe([_tenantA]);
        var audit = _store.Audits.ShouldHaveSingleItem();
        audit.Action.ShouldBe(AuditActions.AdministratorModified);
        audit.Before!.Keys.ShouldBe(["role"]);
        audit.Before["role"].ShouldBe("viewer");
        audit.After!["role"].ShouldBe("operator");
    }

    [Fact]
    public async Task Update_with_no_change_writes_no_audit_record()
    {
        var viewer = _store.Add("v@example.com", AdministratorRole.Viewer);

        await Service().UpdateAsync(viewer.Id, new UpdateAdministratorRequest("viewer", null, null, true, null), CancellationToken.None);

        _store.Audits.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_password_reset_clears_the_lockout_and_audits_only_that_it_happened()
    {
        var viewer = _store.Add("v@example.com", AdministratorRole.Viewer);
        viewer.LockedUntil = _time.GetUtcNow().AddMinutes(10);

        await Service().UpdateAsync(viewer.Id, new UpdateAdministratorRequest(null, null, null, null, "brand-new-password"), CancellationToken.None);

        viewer.LockedUntil.ShouldBeNull();
        var audit = _store.Audits.ShouldHaveSingleItem();
        audit.After!["passwordChanged"].ShouldBe(true);
        AuditRecordFactory.ShapeDetails(audit).ShouldNotContain("brand-new-password");
    }

    [Fact]
    public async Task The_last_active_owner_cannot_be_demoted_or_deactivated()
    {
        var owner = _store.Add("owner@example.com", AdministratorRole.Owner);

        var demote = await Service().UpdateAsync(owner.Id, new UpdateAdministratorRequest("operator", null, null, null, null), CancellationToken.None);
        var deactivate = await Service().UpdateAsync(owner.Id, new UpdateAdministratorRequest(null, null, null, false, null), CancellationToken.None);

        demote.Error.Code.ShouldBe(ErrorCode.Conflict);
        deactivate.Error.Code.ShouldBe(ErrorCode.Conflict);
        owner.Role.ShouldBe(AdministratorRole.Owner);
        owner.Active.ShouldBeTrue();
    }

    [Fact]
    public async Task An_owner_can_be_demoted_when_another_active_owner_exists()
    {
        var first = _store.Add("one@example.com", AdministratorRole.Owner);
        _store.Add("two@example.com", AdministratorRole.Owner);

        var result = await Service().UpdateAsync(first.Id, new UpdateAdministratorRequest(null, null, null, false, null), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Active.ShouldBeFalse();
    }

    [Fact]
    public async Task Updating_a_missing_administrator_is_not_found()
    {
        var result = await Service().UpdateAsync(Guid.CreateVersion7(), new UpdateAdministratorRequest("viewer", null, null, null, null), CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.NotFound);
    }
}

public sealed class OwnerBootstrapperTests
{
    private readonly FakeAdministratorStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));

    private OwnerBootstrapper Bootstrapper() => new(_store, new PasswordHasher<Administrator>(), new VersionSevenGuids(), _time);

    [Fact]
    public async Task Creates_an_all_tenants_owner_when_no_administrator_exists()
    {
        var result = await Bootstrapper().BootstrapAsync("Owner@Example.com", "a-long-enough-password", CancellationToken.None);

        result.Value.ShouldBe(BootstrapOutcome.Created);
        var owner = _store.Administrators.Values.ShouldHaveSingleItem();
        owner.Email.ShouldBe("owner@example.com");
        owner.Role.ShouldBe(AdministratorRole.Owner);
        owner.AllTenants.ShouldBeTrue();
        _store.Audits.ShouldHaveSingleItem().Action.ShouldBe(AuditActions.AdministratorBootstrapped);
    }

    [Fact]
    public async Task Running_twice_creates_one_owner()
    {
        await Bootstrapper().BootstrapAsync("owner@example.com", "a-long-enough-password", CancellationToken.None);

        var second = await Bootstrapper().BootstrapAsync("other@example.com", "another-long-password", CancellationToken.None);

        second.Value.ShouldBe(BootstrapOutcome.AlreadyBootstrapped);
        _store.Administrators.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Nothing_configured_is_not_an_error()
    {
        (await Bootstrapper().BootstrapAsync(null, null, CancellationToken.None)).Value.ShouldBe(BootstrapOutcome.NotConfigured);
    }

    [Fact]
    public async Task Invalid_settings_are_named_by_variable_and_never_echo_the_password()
    {
        var result = await Bootstrapper().BootstrapAsync("not-an-email", "short-pw", CancellationToken.None);

        result.Error.Code.ShouldBe(ErrorCode.ValidationFailed);
        result.Error.FieldErrors!.Keys.ShouldBe(["MODELSWITCH__Bootstrap__OwnerEmail", "MODELSWITCH__Bootstrap__OwnerPassword"], ignoreOrder: true);
        result.Error.Message.ShouldNotContain("short-pw");
        _store.Administrators.ShouldBeEmpty();
    }

    [Fact]
    public async Task Invalid_settings_are_ignored_once_an_administrator_exists()
    {
        _store.Add("owner@example.com", AdministratorRole.Owner);

        var result = await Bootstrapper().BootstrapAsync("bad", "bad", CancellationToken.None);

        result.Value.ShouldBe(BootstrapOutcome.AlreadyBootstrapped);
    }
}

public sealed class AdministratorSessionServiceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeAdministratorStore _administrators = new();
    private readonly FakeSessionStore _sessions = new();

    private AdministratorSessionService Service() => new(_sessions, _administrators, _time);

    [Fact]
    public async Task A_new_session_resolves_and_only_the_hash_of_its_key_is_stored()
    {
        var owner = _administrators.Add("owner@example.com", AdministratorRole.Owner);

        var created = await Service().CreateAsync(owner.Id, CancellationToken.None);

        _sessions.Sessions.Keys.ShouldNotContain(created.SessionKey);
        var resolved = await Service().ResolveAsync(created.SessionKey, CancellationToken.None);
        resolved.ShouldNotBeNull().Administrator.Id.ShouldBe(owner.Id);
    }

    [Fact]
    public async Task An_unknown_or_empty_key_does_not_resolve()
    {
        (await Service().ResolveAsync("nope", CancellationToken.None)).ShouldBeNull();
        (await Service().ResolveAsync(string.Empty, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_session_expires_eight_hours_after_last_use_but_renewal_slides_it()
    {
        var owner = _administrators.Add("owner@example.com", AdministratorRole.Owner);
        var key = (await Service().CreateAsync(owner.Id, CancellationToken.None)).SessionKey;

        _time.Advance(TimeSpan.FromHours(7));
        (await Service().RenewAsync(key, CancellationToken.None)).ShouldNotBeNull();
        _time.Advance(TimeSpan.FromHours(7));
        (await Service().ResolveAsync(key, CancellationToken.None)).ShouldNotBeNull();

        _time.Advance(TimeSpan.FromHours(9));
        (await Service().ResolveAsync(key, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Renewal_never_passes_the_twenty_four_hour_absolute_limit()
    {
        var owner = _administrators.Add("owner@example.com", AdministratorRole.Owner);
        var key = (await Service().CreateAsync(owner.Id, CancellationToken.None)).SessionKey;

        for (var hour = 0; hour < 23; hour += 7)
        {
            _time.Advance(TimeSpan.FromHours(7));
            await Service().RenewAsync(key, CancellationToken.None);
        }

        _time.Advance(TimeSpan.FromHours(3));
        (await Service().ResolveAsync(key, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Deactivating_the_administrator_ends_the_session_immediately()
    {
        var operatorAdmin = _administrators.Add("op@example.com", AdministratorRole.Operator);
        var key = (await Service().CreateAsync(operatorAdmin.Id, CancellationToken.None)).SessionKey;

        operatorAdmin.Active = false;

        (await Service().ResolveAsync(key, CancellationToken.None)).ShouldBeNull();
        _sessions.Sessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_role_change_applies_to_the_existing_session()
    {
        var viewer = _administrators.Add("v@example.com", AdministratorRole.Viewer);
        var key = (await Service().CreateAsync(viewer.Id, CancellationToken.None)).SessionKey;

        viewer.Role = AdministratorRole.Operator;

        (await Service().ResolveAsync(key, CancellationToken.None))!.Administrator.Role.ShouldBe(AdministratorRole.Operator);
    }

    [Fact]
    public async Task Revoking_ends_the_session()
    {
        var owner = _administrators.Add("owner@example.com", AdministratorRole.Owner);
        var key = (await Service().CreateAsync(owner.Id, CancellationToken.None)).SessionKey;

        await Service().RevokeAsync(key, CancellationToken.None);

        (await Service().ResolveAsync(key, CancellationToken.None)).ShouldBeNull();
    }
}
