using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Registry.Infrastructure.Secrets;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Registry.Infrastructure.Credentials;

/// <summary>
/// <c>db:&lt;uuid&gt;</c>: decrypts the <c>registry.provider_secret</c> row (ADR-0007). Rows are immutable except for
/// re-encryption (the plaintext is unchanged) and a replacement gets a new id, so a cached plaintext is valid for as
/// long as its id is referenced; the short TTL only bounds how long a cleared secret stays in memory.
/// </summary>
internal sealed partial class DbCredentialSource(
    IDbContextFactory<RegistryDbContext> contexts,
    SecretCipher cipher,
    TimeProvider time,
    ILogger<DbCredentialSource> logger) : ICredentialSource
{
    private static readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, (CredentialSecret Secret, DateTimeOffset Expires)> _cache = new();

    public string Scheme => "db";

    public async ValueTask<Result<CredentialSecret>> ResolveAsync(string value, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(value, "D", out var id))
        {
            return Fail("Credential reference db: is malformed.");
        }

        var now = time.GetUtcNow();
        if (_cache.TryGetValue(id, out var cached) && cached.Expires > now)
        {
            return cached.Secret;
        }

        if (!cipher.IsConfigured)
        {
            LogNoMasterKey(logger, id);
            return Fail("A db: credential is referenced but the master key is not configured.");
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ProviderSecrets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            LogMissing(logger, id);
            return Fail("Credential reference db: points to a secret that does not exist.");
        }

        try
        {
            var secret = new CredentialSecret(cipher.Decrypt(new SealedSecret(row.Ciphertext, row.Nonce, row.Tag, row.KeyVersion), row.ProviderId));
            _cache[id] = (secret, now + _cacheTtl);
            return secret;
        }
        catch (CryptographicException)
        {
            LogDecryptFailed(logger, id, row.KeyVersion);
            return Fail("A stored db: credential could not be decrypted.");
        }
    }

    private static Result<CredentialSecret> Fail(string message) => new Error(ErrorCode.ProviderUnavailable, message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Provider secret {SecretId} is referenced but the master key is not configured")]
    private static partial void LogNoMasterKey(ILogger logger, Guid secretId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Provider secret {SecretId} is referenced but has no row")]
    private static partial void LogMissing(ILogger logger, Guid secretId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Provider secret {SecretId} (master key version {KeyVersion}) failed authentication or has no matching master key")]
    private static partial void LogDecryptFailed(ILogger logger, Guid secretId, short keyVersion);
}
