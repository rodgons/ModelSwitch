using System.Security.Claims;
using ModelSwitch.Identity.Application;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel.Errors;
using Shouldly;

namespace ModelSwitch.UnitTests.Identity;

/// <summary>The Role permission matrix and Tenant Grant rules (api.md section 3).</summary>
public sealed class RolesAndGrantsTests
{
    private static readonly Guid _tenantA = Guid.CreateVersion7();
    private static readonly Guid _tenantB = Guid.CreateVersion7();

    public static TheoryData<AdministratorRole, AdministratorRole, bool> Matrix() => new()
    {
        { AdministratorRole.Viewer, AdministratorRole.Viewer, true },
        { AdministratorRole.Viewer, AdministratorRole.Operator, false },
        { AdministratorRole.Viewer, AdministratorRole.Owner, false },
        { AdministratorRole.Operator, AdministratorRole.Viewer, true },
        { AdministratorRole.Operator, AdministratorRole.Operator, true },
        { AdministratorRole.Operator, AdministratorRole.Owner, false },
        { AdministratorRole.Owner, AdministratorRole.Viewer, true },
        { AdministratorRole.Owner, AdministratorRole.Operator, true },
        { AdministratorRole.Owner, AdministratorRole.Owner, true },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void A_role_satisfies_a_policy_only_when_it_is_at_least_the_required_role(AdministratorRole actual, AdministratorRole required, bool expected)
    {
        AdministratorRoles.Satisfies(actual, required).ShouldBe(expected);
        AdministratorRoles.AtLeast(required).Contains(AdministratorRoles.ToWire(actual)).ShouldBe(expected);
    }

    [Fact]
    public void Policy_role_lists_are_viewer_operator_owner_widening_downwards()
    {
        AdministratorRoles.AtLeast(AdministratorRole.Viewer).ShouldBe(["viewer", "operator", "owner"]);
        AdministratorRoles.AtLeast(AdministratorRole.Operator).ShouldBe(["operator", "owner"]);
        AdministratorRoles.AtLeast(AdministratorRole.Owner).ShouldBe(["owner"]);
    }

    [Theory]
    [InlineData("owner", AdministratorRole.Owner)]
    [InlineData("operator", AdministratorRole.Operator)]
    [InlineData("viewer", AdministratorRole.Viewer)]
    public void Roles_round_trip_through_their_wire_value(string wire, AdministratorRole role)
    {
        AdministratorRoles.TryParse(wire, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(role);
        AdministratorRoles.ToWire(role).ShouldBe(wire);
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("admin")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_roles_do_not_parse(string? wire) => AdministratorRoles.TryParse(wire, out _).ShouldBeFalse();

    [Fact]
    public void An_all_tenants_grant_allows_any_tenant()
    {
        TenantGrant.AllTenants.Allows(_tenantA).ShouldBeTrue();
        TenantGrant.AllTenants.Allows(Guid.CreateVersion7()).ShouldBeTrue();
    }

    [Fact]
    public void An_explicit_grant_allows_only_its_tenants_and_none_allows_nothing()
    {
        var grant = TenantGrant.Only([_tenantA]);

        grant.Allows(_tenantA).ShouldBeTrue();
        grant.Allows(_tenantB).ShouldBeFalse();
        TenantGrant.None.Allows(_tenantA).ShouldBeFalse();
    }

    [Fact]
    public void A_tenant_outside_the_grant_is_not_found_not_forbidden()
    {
        var operatorAdmin = new AdministratorPrincipal(Guid.CreateVersion7(), "op@example.com", AdministratorRole.Operator, TenantGrant.Only([_tenantA]));
        var checker = new TenantGrantChecker(new FixedCurrentAdministrator(operatorAdmin));

        checker.CheckAccess(_tenantA).IsSuccess.ShouldBeTrue();
        var outside = checker.CheckAccess(_tenantB);
        outside.IsFailure.ShouldBeTrue();
        outside.Error.Code.ShouldBe(ErrorCode.NotFound);
        outside.Error.Code.HttpStatus.ShouldBe(404);
    }

    [Fact]
    public void An_owner_passes_the_grant_check_for_every_tenant()
    {
        var owner = new AdministratorPrincipal(Guid.CreateVersion7(), "owner@example.com", AdministratorRole.Owner, TenantGrant.AllTenants);

        new TenantGrantChecker(new FixedCurrentAdministrator(owner)).CheckAccess(_tenantB).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Nobody_signed_in_is_unauthenticated()
    {
        var result = new TenantGrantChecker(new FixedCurrentAdministrator(null)).CheckAccess(_tenantA);

        result.Error.Code.ShouldBe(ErrorCode.Unauthenticated);
    }

    [Fact]
    public void The_principal_round_trips_through_claims_and_only_for_the_administrator_scheme()
    {
        var administrator = new AdministratorPrincipal(Guid.CreateVersion7(), "op@example.com", AdministratorRole.Operator, TenantGrant.Only([_tenantA, _tenantB]));

        var restored = AdminClaims.FromPrincipal(AdminClaims.ToPrincipal(administrator));

        restored.ShouldNotBeNull();
        restored.Id.ShouldBe(administrator.Id);
        restored.Role.ShouldBe(AdministratorRole.Operator);
        restored.Grant.IsAll.ShouldBeFalse();
        restored.Grant.TenantIds.ShouldBe([_tenantA, _tenantB], ignoreOrder: true);

        var apiKeyShaped = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, administrator.Id.ToString()), new Claim(AdminClaims.Role, "owner")], "ApiKey"));
        AdminClaims.FromPrincipal(apiKeyShaped).ShouldBeNull();
        AdminClaims.FromPrincipal(new ClaimsPrincipal(new ClaimsIdentity())).ShouldBeNull();
    }

    [Fact]
    public void An_owner_principal_carries_the_all_tenants_claim()
    {
        var owner = new AdministratorPrincipal(Guid.CreateVersion7(), "owner@example.com", AdministratorRole.Owner, TenantGrant.AllTenants);

        AdminClaims.FromPrincipal(AdminClaims.ToPrincipal(owner))!.Grant.IsAll.ShouldBeTrue();
    }
}
