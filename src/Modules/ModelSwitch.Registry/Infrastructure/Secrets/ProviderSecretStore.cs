using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Registry.Application;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Registry.Domain;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Configuration;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Registry.Infrastructure.Secrets;

/// <summary>
/// Write-only management of <c>db:</c> Provider secrets. Every change replaces the row (new id), repoints the Provider's
/// Credential Reference and bumps the config version in one transaction, so the snapshot's cached reference and the
/// stored secret never disagree.
/// </summary>
internal sealed partial class ProviderSecretStore(
    IDbContextFactory<RegistryDbContext> contexts,
    SecretCipher cipher,
    TimeProvider time,
    IGuidGenerator guids,
    IAuditWriter audit,
    ILogger<ProviderSecretStore> logger) : IProviderSecretStore
{
    public const string DbScheme = "db:";
    public const int MaxSecretLength = 4096;

    public async Task<Result<ProviderSecretStatus>> SetAsync(Guid providerId, string secret, CancellationToken cancellationToken)
    {
        var invalid = Validate(secret);
        if (invalid is not null)
        {
            return invalid;
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var provider = await db.Providers.FirstOrDefaultAsync(p => p.Id == providerId, cancellationToken);
        if (provider is null)
        {
            return ProviderNotFound();
        }

        var status = await ApplyAsync(db, provider, secret, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(SecretAuditEntry(provider), transaction.GetDbTransaction(), cancellationToken);
        await BumpAsync(db, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        LogSecretSet(logger, providerId, cipher.CurrentKeyVersion);
        return status;
    }

    /// <summary>The reasons a secret cannot be stored, or null. Checked before any transaction starts.</summary>
    internal Error? Validate(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > MaxSecretLength)
        {
            return new Error(ErrorCode.ValidationFailed, $"The secret must be 1 to {MaxSecretLength} characters.");
        }

        return cipher.IsConfigured ? null : new Error(ErrorCode.InternalError, "The master key is not configured.");
    }

    /// <summary>
    /// Encrypts <paramref name="secret"/> into a new tracked row, removes the row the Provider referenced and repoints the
    /// Provider (tracked in <paramref name="db"/>). The caller saves, audits and bumps the config version in its own
    /// transaction; <see cref="Validate"/> must have passed.
    /// </summary>
    internal async Task<ProviderSecretStatus> ApplyAsync(RegistryDbContext db, Provider provider, string secret, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var sealedSecret = cipher.Encrypt(secret, provider.Id);
        var row = new ProviderSecret
        {
            Id = guids.NewGuid(),
            ProviderId = provider.Id,
            Ciphertext = sealedSecret.Ciphertext,
            Nonce = sealedSecret.Nonce,
            Tag = sealedSecret.Tag,
            KeyVersion = sealedSecret.KeyVersion,
            CreatedAt = now,
        };

        await RemoveReferencedRowAsync(db, provider.CredentialReference, cancellationToken);

        db.ProviderSecrets.Add(row);
        provider.CredentialReference = DbScheme + row.Id.ToString("D", CultureInfo.InvariantCulture);
        provider.UpdatedAt = now;
        return new ProviderSecretStatus("db", true, now);
    }

    /// <summary>The <c>provider.secret_replaced</c> Audit Record. It carries no secret material.</summary>
    internal AuditEntry SecretAuditEntry(Provider provider)
        => new(RegistryAuditActions.ProviderSecretReplaced, "provider", provider.Id.ToString("D", CultureInfo.InvariantCulture))
        {
            Details = new Dictionary<string, object?> { ["name"] = provider.Name, ["credentialKind"] = "db", ["keyVersion"] = (int)cipher.CurrentKeyVersion },
        };

    public async Task<Result<ProviderSecretStatus>> ClearAsync(Guid providerId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var provider = await db.Providers.FirstOrDefaultAsync(p => p.Id == providerId, cancellationToken);
        if (provider is null)
        {
            return ProviderNotFound();
        }

        if (provider.CredentialReference is null || !provider.CredentialReference.StartsWith(DbScheme, StringComparison.Ordinal))
        {
            return await StatusAsync(db, provider, cancellationToken);
        }

        await RemoveReferencedRowAsync(db, provider.CredentialReference, cancellationToken);
        provider.CredentialReference = null;
        provider.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await BumpAsync(db, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        LogSecretCleared(logger, providerId);
        return new ProviderSecretStatus("none", false, null);
    }

    public async Task<Result<ProviderSecretStatus>> GetStatusAsync(Guid providerId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var provider = await db.Providers.AsNoTracking().FirstOrDefaultAsync(p => p.Id == providerId, cancellationToken);
        return provider is null ? ProviderNotFound() : await StatusAsync(db, provider, cancellationToken);
    }

    public async Task<Result<int>> ReencryptAllAsync(CancellationToken cancellationToken)
    {
        if (!cipher.IsConfigured)
        {
            return new Error(ErrorCode.InternalError, "The master key is not configured.");
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var stale = await db.ProviderSecrets.Where(s => s.KeyVersion != cipher.CurrentKeyVersion).ToListAsync(cancellationToken);
        foreach (var row in stale)
        {
            try
            {
                var plaintext = cipher.Decrypt(new SealedSecret(row.Ciphertext, row.Nonce, row.Tag, row.KeyVersion), row.ProviderId);
                var resealed = cipher.Encrypt(plaintext, row.ProviderId);
                row.Ciphertext = resealed.Ciphertext;
                row.Nonce = resealed.Nonce;
                row.Tag = resealed.Tag;
                row.KeyVersion = resealed.KeyVersion;
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                LogReencryptFailed(logger, row.Id, row.KeyVersion);
                return new Error(ErrorCode.InternalError, "A stored secret could not be decrypted with the configured master keys.");
            }
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        LogReencrypted(logger, stale.Count, cipher.CurrentKeyVersion);
        return stale.Count;
    }

    private static Error ProviderNotFound() => new(ErrorCode.NotFound, "Provider not found.");

    private static async Task<ProviderSecretStatus> StatusAsync(RegistryDbContext db, Provider provider, CancellationToken cancellationToken)
    {
        var reference = provider.CredentialReference;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return new ProviderSecretStatus("none", false, null);
        }

        if (!TryParseSecretId(reference, out var secretId))
        {
            return new ProviderSecretStatus("env", true, null);
        }

        var created = await db.ProviderSecrets.AsNoTracking().Where(s => s.Id == secretId).Select(s => (DateTimeOffset?)s.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        return new ProviderSecretStatus("db", true, created);
    }

    internal static async Task RemoveReferencedRowAsync(RegistryDbContext db, string? reference, CancellationToken cancellationToken)
    {
        if (reference is not null && TryParseSecretId(reference, out var id))
        {
            await db.ProviderSecrets.Where(s => s.Id == id).ExecuteDeleteAsync(cancellationToken);
        }
    }

    private static async Task BumpAsync(RegistryDbContext db, CancellationToken cancellationToken)
        => await ConfigVersion.BumpAsync(
            db.Database.GetDbConnection(),
            db.Database.CurrentTransaction?.GetDbTransaction(),
            cancellationToken);

    internal static bool TryParseSecretId(string reference, out Guid id)
    {
        id = Guid.Empty;
        return reference.StartsWith(DbScheme, StringComparison.Ordinal)
            && Guid.TryParseExact(reference.AsSpan(DbScheme.Length), "D", out id);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Provider {ProviderId} secret set under master key version {KeyVersion}")]
    private static partial void LogSecretSet(ILogger logger, Guid providerId, short keyVersion);

    [LoggerMessage(Level = LogLevel.Information, Message = "Provider {ProviderId} secret cleared")]
    private static partial void LogSecretCleared(ILogger logger, Guid providerId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-encrypted {Count} Provider secret(s) under master key version {KeyVersion}")]
    private static partial void LogReencrypted(ILogger logger, int count, short keyVersion);

    [LoggerMessage(Level = LogLevel.Error, Message = "Provider secret {SecretId} (master key version {KeyVersion}) could not be decrypted during re-encryption")]
    private static partial void LogReencryptFailed(ILogger logger, Guid secretId, short keyVersion);
}
