namespace ModelSwitch.Identity.Contracts;

/// <summary>The Scopes an API Key can carry (MVP, docs/api.md section 1).</summary>
public static class ApiKeyScopes
{
    /// <summary>Chat completions and the model list.</summary>
    public const string Inference = "inference";

    /// <summary>The model list only.</summary>
    public const string ModelsRead = "models:read";

    public static IReadOnlyList<string> All { get; } = [Inference, ModelsRead];
}
