namespace ModelSwitch.Identity.Contracts;

/// <summary>The identity established by a valid API Key. The Tenant always comes from here, never from a request field.</summary>
public sealed record AuthenticatedApiKey(Guid ApiKeyId, Guid TenantId, string KeyPrefix, IReadOnlyList<string> Scopes);
