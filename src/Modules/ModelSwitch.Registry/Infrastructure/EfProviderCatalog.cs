using Microsoft.EntityFrameworkCore;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Registry.Domain;

namespace ModelSwitch.Registry.Infrastructure;

internal sealed class EfProviderCatalog(IDbContextFactory<RegistryDbContext> contexts) : IProviderCatalog
{
    public async Task<ProviderModelTarget?> FindEnabledAsync(string providerName, string providerModelIdentifier, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var found = await (
            from model in db.ProviderModels.AsNoTracking()
            join provider in db.Providers.AsNoTracking() on model.ProviderId equals provider.Id
            where provider.Name == providerName
                && model.ProviderModelIdentifier == providerModelIdentifier
                && provider.Enabled
                && model.Enabled
            select new { Model = model, Provider = provider })
            .FirstOrDefaultAsync(cancellationToken);

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
