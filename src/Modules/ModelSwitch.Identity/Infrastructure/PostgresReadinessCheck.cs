using Microsoft.EntityFrameworkCore;
using ModelSwitch.SharedKernel.Http;

namespace ModelSwitch.Identity.Infrastructure;

/// <summary>PostgreSQL reachability for <c>GET /ready</c>, through the app-role connection. Fails fast after 2 s.</summary>
internal sealed class PostgresReadinessCheck(IDbContextFactory<IdentityDbContext> contexts) : IReadinessCheck
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(2);

    public string Name => "postgres";

    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_timeout);
        try
        {
            await using var db = await contexts.CreateDbContextAsync(limit.Token);
            return await db.Database.CanConnectAsync(limit.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
