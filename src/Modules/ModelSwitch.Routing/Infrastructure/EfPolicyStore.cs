using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelSwitch.Routing.Contracts;

namespace ModelSwitch.Routing.Infrastructure;

/// <summary>Reads the latest Policy Version of an alias's policy. The runtime configuration snapshot will replace per-request reads.</summary>
internal sealed class EfPolicyStore(IDbContextFactory<RoutingDbContext> contexts, IPolicyDefinitionValidator validator) : IPolicyStore
{
    public async Task<PolicySnapshot?> GetActiveForAliasAsync(string modelAlias, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await (
            from a in db.Aliases.AsNoTracking()
            join p in db.Policies.AsNoTracking() on a.PolicyId equals p.Id
            join v in db.PolicyVersions.AsNoTracking() on new { PolicyId = p.Id, Version = p.LatestVersion } equals new { v.PolicyId, v.Version }
            where a.Alias == modelAlias
            select new { p.Id, p.Name, v.Version, v.DefinitionJson, v.ResolvedCandidatesJson })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var resolved = JsonSerializer.Deserialize<Dictionary<string, Guid>>(row.ResolvedCandidatesJson) ?? [];
        var providers = resolved.Keys.Select(k => k[..k.IndexOf('/', StringComparison.Ordinal)]).ToHashSet(StringComparer.Ordinal);
        providers.UnionWith(AllowedProvidersOf(row.DefinitionJson));

        // The stored version was validated when it was saved, so re-reading it against its own resolved names succeeds.
        var result = validator.Validate(row.DefinitionJson, new PolicyReferenceData(resolved, providers));
        return result.Definition is { } definition
            ? new PolicySnapshot(row.Id, row.Name, row.Version, definition)
            : throw new InvalidOperationException($"Stored version {row.Version} of routing policy {row.Id} no longer validates.");
    }

    private static List<string> AllowedProvidersOf(string definitionJson)
    {
        using var document = JsonDocument.Parse(definitionJson);
        if (document.RootElement.TryGetProperty("allowedProviders", out var allowed) && allowed.ValueKind == JsonValueKind.Array)
        {
            return allowed.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!).ToList();
        }

        return [];
    }
}
