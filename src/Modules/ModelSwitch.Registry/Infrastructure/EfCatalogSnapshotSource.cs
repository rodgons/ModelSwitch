using Microsoft.EntityFrameworkCore;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Registry.Domain;

namespace ModelSwitch.Registry.Infrastructure;

internal sealed class EfCatalogSnapshotSource(IDbContextFactory<RegistryDbContext> contexts, TimeProvider time) : ICatalogSnapshotSource
{
    public async Task<CatalogSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var now = time.GetUtcNow();

        var models = await (
            from model in db.ProviderModels.AsNoTracking()
            join provider in db.Providers.AsNoTracking() on model.ProviderId equals provider.Id
            select new
            {
                model.Id,
                ProviderId = provider.Id,
                ProviderName = provider.Name,
                model.ProviderModelIdentifier,
                ProviderEnabled = provider.Enabled,
                ModelEnabled = model.Enabled,
                model.CapabilitiesJson,
                provider.Endpoint,
                provider.AdapterType,
                provider.CredentialReference,
                provider.RequestTimeoutMs,
            })
            .ToListAsync(cancellationToken);

        // The Price in force is the row with the latest effective_from not after now (data-model.md).
        var prices = (await db.ModelPrices.AsNoTracking()
                .Where(p => p.EffectiveFrom <= now)
                .OrderBy(p => p.EffectiveFrom)
                .ToListAsync(cancellationToken))
            .GroupBy(p => p.ProviderModelId)
            .ToDictionary(g => g.Key, g => g.Last());

        var entries = models
            .Select(m => new CatalogEntry(
                m.Id,
                m.ProviderId,
                m.ProviderName,
                m.ProviderModelIdentifier,
                m.ProviderEnabled,
                m.ModelEnabled,
                m.CapabilitiesJson,
                prices.TryGetValue(m.Id, out var price) ? new CatalogPrice(price.InputPerMillion, price.CachedInputPerMillion, price.OutputPerMillion) : null,
                Uri.TryCreate(m.Endpoint, UriKind.Absolute, out var endpoint)
                    ? new ProviderModelTarget(
                        m.Id,
                        m.ProviderId,
                        m.ProviderName,
                        m.AdapterType == AdapterType.Ollama ? "ollama" : "openai_compatible",
                        endpoint,
                        m.CredentialReference,
                        TimeSpan.FromMilliseconds(m.RequestTimeoutMs),
                        m.ProviderModelIdentifier)
                    : null))
            .ToList();
        return new CatalogSnapshot(entries);
    }
}
