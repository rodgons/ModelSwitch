using System.Collections.Concurrent;
using ModelSwitch.Identity.Contracts;

namespace ModelSwitch.Identity.Application;

/// <summary>
/// In-memory cache of verified API Keys, keyed by the key's HMAC (never the raw key). Entries live for at most
/// <see cref="MaxTtl"/>, which bounds how long a revoked key keeps working on this instance (ADR-0006).
/// Only successes are cached, so a flood of bad keys cannot grow it.
/// </summary>
internal sealed class VerifiedKeyCache(TimeProvider time)
{
    public static readonly TimeSpan MaxTtl = TimeSpan.FromSeconds(30);

    private const int _maxEntries = 10_000;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool TryGet(byte[] keyHash, out AuthenticatedApiKey key)
    {
        if (_entries.TryGetValue(Convert.ToBase64String(keyHash), out var entry))
        {
            if (time.GetUtcNow() < entry.ExpiresAt)
            {
                key = entry.Key;
                return true;
            }

            _entries.TryRemove(new KeyValuePair<string, Entry>(Convert.ToBase64String(keyHash), entry));
        }

        key = null!;
        return false;
    }

    /// <summary>Caches <paramref name="key"/> for <see cref="MaxTtl"/>, or until <paramref name="keyExpiresAt"/> if sooner.</summary>
    public void Set(byte[] keyHash, AuthenticatedApiKey key, DateTimeOffset? keyExpiresAt)
    {
        var now = time.GetUtcNow();
        var expiresAt = now + MaxTtl;
        if (keyExpiresAt is { } hardLimit && hardLimit < expiresAt)
        {
            expiresAt = hardLimit;
        }

        if (expiresAt <= now)
        {
            return;
        }

        if (_entries.Count >= _maxEntries)
        {
            foreach (var pair in _entries)
            {
                if (pair.Value.ExpiresAt <= now)
                {
                    _entries.TryRemove(pair);
                }
            }

            if (_entries.Count >= _maxEntries)
            {
                return;
            }
        }

        _entries[Convert.ToBase64String(keyHash)] = new Entry(key, expiresAt);
    }

    /// <summary>
    /// Drops every entry on this instance, so the next request for each key goes back to the database. Called after a key is
    /// revoked or a Tenant is suspended, so the instance that took the admin write applies it at once.
    /// </summary>
    public void Clear() => _entries.Clear();

    private sealed record Entry(AuthenticatedApiKey Key, DateTimeOffset ExpiresAt);
}
