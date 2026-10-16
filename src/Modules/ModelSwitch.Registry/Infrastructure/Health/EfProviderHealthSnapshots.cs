using Microsoft.EntityFrameworkCore;
using ModelSwitch.Registry.Contracts;

namespace ModelSwitch.Registry.Infrastructure.Health;

internal sealed class EfProviderHealthSnapshots(IDbContextFactory<RegistryDbContext> contexts) : IProviderHealthSnapshots
{
    public async Task<IReadOnlyList<ProviderHealthSnapshotRow>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.ProviderHealthSnapshots.AsNoTracking()
            .OrderBy(s => s.ProviderModelId).ThenBy(s => s.InstanceId)
            .Select(s => new ProviderHealthSnapshotRow(s.InstanceId, s.ProviderModelId, s.CircuitState, s.WindowFailureRate, s.WindowCalls, s.CapturedAt))
            .ToListAsync(cancellationToken);
    }
}
