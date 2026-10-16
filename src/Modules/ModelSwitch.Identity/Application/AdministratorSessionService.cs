using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Identity.Domain;

namespace ModelSwitch.Identity.Application;

internal sealed class AdministratorSessionService(
    ISessionStore sessions,
    IAdministratorStore administrators,
    TimeProvider time) : IAdministratorSessions
{
    private const int _keyBytes = 32;

    public async Task<SessionInfo> CreateAsync(Guid administratorId, CancellationToken cancellationToken)
    {
        var administrator = await administrators.FindByIdAsync(administratorId, cancellationToken)
            ?? throw new InvalidOperationException("The Administrator for a new session does not exist.");

        var key = Base64UrlTextEncoder.Encode(RandomNumberGenerator.GetBytes(_keyBytes));
        var now = time.GetUtcNow();
        var session = AdministratorSession.Start(HashKey(key), administratorId, now);
        await sessions.AddAsync(session, now, cancellationToken);
        return new SessionInfo(key, administrator.ToPrincipal(), session.CreatedAt, session.ExpiresAt);
    }

    public async Task<SessionInfo?> ResolveAsync(string sessionKey, CancellationToken cancellationToken)
        => (await LoadAsync(sessionKey, cancellationToken))?.Info;

    public async Task<SessionInfo?> RenewAsync(string sessionKey, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(sessionKey, cancellationToken);
        if (loaded is null)
        {
            return null;
        }

        loaded.Session.Renew(time.GetUtcNow());
        await sessions.UpdateExpiryAsync(loaded.Session.Id, loaded.Session.ExpiresAt, cancellationToken);
        return loaded.Info with { ExpiresAt = loaded.Session.ExpiresAt };
    }

    public Task RevokeAsync(string sessionKey, CancellationToken cancellationToken)
        => sessions.DeleteAsync(HashKey(sessionKey), cancellationToken);

    internal static string HashKey(string sessionKey)
        => Base64UrlTextEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(sessionKey)));

    private sealed record LoadedSession(AdministratorSession Session, SessionInfo Info);

    private async Task<LoadedSession?> LoadAsync(string sessionKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(sessionKey))
        {
            return null;
        }

        var id = HashKey(sessionKey);
        var session = await sessions.FindAsync(id, cancellationToken);
        if (session is null)
        {
            return null;
        }

        if (session.IsExpired(time.GetUtcNow()))
        {
            await sessions.DeleteAsync(id, cancellationToken);
            return null;
        }

        // Rebuilt on every use: a Role change or a deactivation takes effect on the next request.
        var administrator = await administrators.FindByIdAsync(session.AdministratorId, cancellationToken);
        if (administrator is not { Active: true })
        {
            await sessions.DeleteAsync(id, cancellationToken);
            return null;
        }

        return new LoadedSession(session, new SessionInfo(sessionKey, administrator.ToPrincipal(), session.CreatedAt, session.ExpiresAt));
    }
}
