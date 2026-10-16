using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelSwitch.Routing.Contracts;

namespace ModelSwitch.Routing.Infrastructure;

/// <summary>
/// Reads Policy Versions: the latest of an alias's policy, every policy's latest for the configuration snapshot, and any one version
/// by number (for a run that froze it). The request hot path reads the snapshot instead.
/// </summary>
internal sealed class EfPolicyStore(IDbContextFactory<RoutingDbContext> contexts, IPolicyDefinitionValidator validator) : IPolicyStore, IPolicyCatalogSource
{
    // Policy Versions are immutable (data-model.md), so a version read once never changes. Bounded: cleared when it fills up.
    private const int _versionCacheCapacity = 256;
    private readonly ConcurrentDictionary<(Guid PolicyId, int Version), PolicySnapshot> _versions = new();

    public async Task<PolicySnapshot?> GetVersionAsync(Guid policyId, int version, CancellationToken cancellationToken)
    {
        if (_versions.TryGetValue((policyId, version), out var cached))
        {
            return cached;
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await (
            from p in db.Policies.AsNoTracking()
            join v in db.PolicyVersions.AsNoTracking() on p.Id equals v.PolicyId
            where p.Id == policyId && v.Version == version
            select new { p.Id, p.Name, v.Version, v.DefinitionJson, v.ResolvedCandidatesJson })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var snapshot = ToSnapshot(row.Id, row.Name, row.Version, row.DefinitionJson, row.ResolvedCandidatesJson);
        if (_versions.Count >= _versionCacheCapacity)
        {
            _versions.Clear();
        }

        _versions[(policyId, version)] = snapshot;
        return snapshot;
    }

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

        return row is null ? null : ToSnapshot(row.Id, row.Name, row.Version, row.DefinitionJson, row.ResolvedCandidatesJson);
    }

    public async Task<PolicyCatalog> LoadAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var aliases = await db.Aliases.AsNoTracking().Select(a => new { a.Alias, a.PolicyId }).ToListAsync(cancellationToken);
        var versions = await (
            from p in db.Policies.AsNoTracking()
            join v in db.PolicyVersions.AsNoTracking() on new { PolicyId = p.Id, Version = p.LatestVersion } equals new { v.PolicyId, v.Version }
            select new { p.Id, p.Name, v.Version, v.DefinitionJson, v.ResolvedCandidatesJson })
            .ToListAsync(cancellationToken);

        var policies = versions
            .Select(v => ToSnapshot(v.Id, v.Name, v.Version, v.DefinitionJson, v.ResolvedCandidatesJson))
            .ToDictionary(p => p.PolicyId);
        return new PolicyCatalog(aliases.ToDictionary(a => a.Alias, a => a.PolicyId, StringComparer.Ordinal), policies);
    }

    private PolicySnapshot ToSnapshot(Guid policyId, string name, int version, string definitionJson, string resolvedCandidatesJson)
    {
        var resolved = JsonSerializer.Deserialize<Dictionary<string, Guid>>(resolvedCandidatesJson) ?? [];
        var providers = resolved.Keys.Select(k => k[..k.IndexOf('/', StringComparison.Ordinal)]).ToHashSet(StringComparer.Ordinal);
        providers.UnionWith(AllowedProvidersOf(definitionJson));

        // The stored version was validated when it was saved, so re-reading it against its own resolved names succeeds.
        var result = validator.Validate(definitionJson, new PolicyReferenceData(resolved, providers));
        return result.Definition is { } definition
            ? new PolicySnapshot(policyId, name, version, definition)
            : throw new InvalidOperationException($"Stored version {version} of routing policy {policyId} no longer validates.");
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
