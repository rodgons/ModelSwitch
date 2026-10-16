namespace ModelSwitch.Routing.Application;

/// <summary>Routing's audited actions (security.md section 3), named <c>noun.verb_past</c>.</summary>
internal static class RoutingAuditActions
{
    public const string PolicyCreated = "routing_policy.created";
    public const string PolicyVersioned = "routing_policy.versioned";
    public const string AliasBound = "model_alias.bound";
    public const string AliasUnbound = "model_alias.unbound";
}
