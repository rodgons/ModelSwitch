using System.Net;
using System.Text.Json.Nodes;
using ModelSwitch.Identity.Application;
using ModelSwitch.Identity.Contracts;
using Shouldly;

namespace ModelSwitch.UnitTests.Identity;

public sealed class AuditRecordFactoryTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid _requestId = Guid.CreateVersion7();

    private static AuditCallContext Http(Guid? actor = null) => new(actor, IPAddress.Parse("203.0.113.9"), _requestId, "0af7651916cd43dd8448eb211c80319c");

    [Fact]
    public void A_record_carries_actor_action_target_tenant_request_id_trace_and_source()
    {
        var actor = Guid.CreateVersion7();
        var tenant = Guid.CreateVersion7();
        var id = Guid.CreateVersion7();

        var record = AuditRecordFactory.Create(
            new AuditEntry("api_key.created", "api_key", "key-1") { TenantId = tenant },
            Http(actor),
            id,
            _now);

        record.Id.ShouldBe(id);
        record.OccurredAt.ShouldBe(_now);
        record.AdministratorId.ShouldBe(actor);
        record.Action.ShouldBe("api_key.created");
        record.TargetType.ShouldBe("api_key");
        record.TargetId.ShouldBe("key-1");
        record.TenantId.ShouldBe(tenant);
        record.SourceIp.ShouldBe(IPAddress.Parse("203.0.113.9"));
        record.RequestId.ShouldBe(_requestId);
        record.TraceId.ShouldBe("0af7651916cd43dd8448eb211c80319c");
    }

    [Fact]
    public void An_explicit_actor_wins_over_the_request_actor_and_outside_a_request_there_is_none()
    {
        var explicitActor = Guid.CreateVersion7();

        AuditRecordFactory.Create(new AuditEntry("administrator.signed_in", "administrator", null) { ActorAdministratorId = explicitActor }, Http(Guid.CreateVersion7()), Guid.CreateVersion7(), _now)
            .AdministratorId.ShouldBe(explicitActor);
        AuditRecordFactory.Create(new AuditEntry("administrator.bootstrapped", "administrator", null), AuditCallContext.System, Guid.CreateVersion7(), _now)
            .AdministratorId.ShouldBeNull();
    }

    [Fact]
    public void Before_and_after_summaries_become_nested_json()
    {
        var entry = new AuditEntry("tenant.suspended", "tenant", "t")
        {
            Before = new Dictionary<string, object?> { ["status"] = "active" },
            After = new Dictionary<string, object?> { ["status"] = "suspended", ["tenantIds"] = new List<Guid> { _requestId } },
            Details = new Dictionary<string, object?> { ["reason"] = "abuse" },
        };

        var json = JsonNode.Parse(AuditRecordFactory.ShapeDetails(entry))!;

        json["before"]!["status"]!.GetValue<string>().ShouldBe("active");
        json["after"]!["status"]!.GetValue<string>().ShouldBe("suspended");
        json["after"]!["tenantIds"]![0]!.GetValue<string>().ShouldBe(_requestId.ToString());
        json["reason"]!.GetValue<string>().ShouldBe("abuse");
    }

    [Theory]
    [InlineData("password")]
    [InlineData("newPassword")]
    [InlineData("passwordHash")]
    [InlineData("secret")]
    [InlineData("providerSecret")]
    [InlineData("apiKey")]
    [InlineData("api_key")]
    [InlineData("key")]
    [InlineData("accessToken")]
    [InlineData("credential")]
    [InlineData("prompt")]
    [InlineData("completion")]
    [InlineData("content")]
    [InlineData("Authorization")]
    public void Sensitive_looking_keys_are_redacted_whatever_the_caller_passes(string key)
    {
        var entry = new AuditEntry("provider.modified", "provider", "p")
        {
            After = new Dictionary<string, object?> { [key] = "super-secret-value" },
        };

        var shaped = AuditRecordFactory.ShapeDetails(entry);

        shaped.ShouldNotContain("super-secret-value");
        JsonNode.Parse(shaped)!["after"]![key]!.GetValue<string>().ShouldBe(AuditRecordFactory.Redacted);
    }

    [Fact]
    public void A_key_prefix_is_not_a_secret_and_is_kept()
    {
        var entry = new AuditEntry("api_key.created", "api_key", "k") { After = new Dictionary<string, object?> { ["keyPrefix"] = "abcd1234" } };

        JsonNode.Parse(AuditRecordFactory.ShapeDetails(entry))!["after"]!["keyPrefix"]!.GetValue<string>().ShouldBe("abcd1234");
    }

    [Fact]
    public void Long_strings_are_truncated_and_unsupported_values_are_not_serialised()
    {
        var entry = new AuditEntry("provider.modified", "provider", "p")
        {
            After = new Dictionary<string, object?> { ["name"] = new string('x', 5000), ["blob"] = new object() },
        };

        var after = JsonNode.Parse(AuditRecordFactory.ShapeDetails(entry))!["after"]!;

        after["name"]!.GetValue<string>().Length.ShouldBe(AuditRecordFactory.MaxStringLength);
        after["blob"]!.GetValue<string>().ShouldBe("[unsupported]");
    }

    [Fact]
    public void An_oversized_details_document_is_replaced_by_a_marker()
    {
        var many = Enumerable.Range(0, 32).ToDictionary(i => $"k{i}", i => (object?)new string('y', 200));
        var entry = new AuditEntry("provider.modified", "provider", "p") { Before = many, After = many, Details = many };

        AuditRecordFactory.ShapeDetails(entry).ShouldBe("""{"truncated":true}""");
    }

    [Theory]
    [InlineData("created")]
    [InlineData("Api_Key.Created")]
    [InlineData("api key.created")]
    [InlineData("")]
    public void An_action_must_look_like_noun_dot_verb(string action)
    {
        Should.Throw<ArgumentException>(() => AuditRecordFactory.Create(new AuditEntry(action, "x", null), AuditCallContext.System, Guid.CreateVersion7(), _now));
    }
}
