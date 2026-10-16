namespace ModelSwitch.Registry.Application;

/// <summary>Registry's audited actions (security.md section 3), named <c>noun.verb_past</c>.</summary>
internal static class RegistryAuditActions
{
    public const string ProviderCreated = "provider.created";
    public const string ProviderModified = "provider.modified";
    public const string ProviderSecretReplaced = "provider.secret_replaced";
    public const string ProviderModelCreated = "provider_model.created";
    public const string ProviderModelModified = "provider_model.modified";
    public const string ModelPriceCreated = "model_price.created";
}
