using Microsoft.EntityFrameworkCore;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Registry.Domain;

namespace ModelSwitch.Registry.Infrastructure;

internal sealed class EfProviderCatalog(IDbContextFactory<RegistryDbContext> contexts) : IProviderCatalog
{
    public async Task<ProviderModelTarget?> FindEnabledAsync(string providerName, string providerModelIdentifier, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var found = await Enabled(db)
            .Where(f => f.Provider.Name == providerName && f.Model.ProviderModelIdentifier == providerModelIdentifier)
            .FirstOrDefaultAsync(cancellationToken);
        return ToTarget(found);
    }

    public async Task<ProviderModelTarget?> FindEnabledByIdAsync(Guid providerModelId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var found = await Enabled(db).Where(f => f.Model.Id == providerModelId).FirstOrDefaultAsync(cancellationToken);
        return ToTarget(found);
    }

    // A member-init projection (not a constructor), so EF Core can translate the filters composed over it.
    private sealed class Found
    {
        public required ProviderModel Model { get; init; }

        public required Provider Provider { get; init; }
    }

    private static IQueryable<Found> Enabled(RegistryDbContext db)
        => from model in db.ProviderModels.AsNoTracking()
           join provider in db.Providers.AsNoTracking() on model.ProviderId equals provider.Id
           where provider.Enabled && model.Enabled
           select new Found { Model = model, Provider = provider };

    private static ProviderModelTarget? ToTarget(Found? found)
    {
        if (found is null || !Uri.TryCreate(found.Provider.Endpoint, UriKind.Absolute, out var endpoint))
        {
            return null;
        }

        return new ProviderModelTarget(
            found.Model.Id,
            found.Provider.Id,
            found.Provider.Name,
            found.Provider.AdapterType == AdapterType.Ollama ? "ollama" : "openai_compatible",
            endpoint,
            found.Provider.CredentialReference,
            TimeSpan.FromMilliseconds(found.Provider.RequestTimeoutMs),
            found.Model.ProviderModelIdentifier);
    }
}
