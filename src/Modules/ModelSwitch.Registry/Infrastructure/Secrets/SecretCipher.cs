using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ModelSwitch.Registry.Infrastructure.Secrets;

/// <summary>An encrypted secret as stored in <c>registry.provider_secret</c>.</summary>
internal sealed record SealedSecret(byte[] Ciphertext, byte[] Nonce, byte[] Tag, short KeyVersion);

/// <summary>
/// AES-256-GCM over a Provider secret (ADR-0007). A fresh random 96-bit nonce per encryption; the additional
/// authenticated data is the Provider id plus the key version, so a row moved to another Provider, or relabelled with
/// another key version, fails authentication.
/// </summary>
internal sealed class SecretCipher(MasterKeyRing keys)
{
    public const int NonceBytes = 12;
    public const int TagBytes = 16;

    public bool IsConfigured => keys.HasKey;

    public short CurrentKeyVersion => keys.CurrentVersion;

    public SealedSecret Encrypt(string plaintext, Guid providerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        if (!keys.HasKey || !keys.TryGetKey(keys.CurrentVersion, out var key))
        {
            throw new InvalidOperationException("The master key is not configured.");
        }

        var data = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[data.Length];
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var tag = new byte[TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, data, ciphertext, tag, AssociatedData(providerId, keys.CurrentVersion));
            return new SealedSecret(ciphertext, nonce, tag, keys.CurrentVersion);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    /// <summary>Decrypts <paramref name="sealedSecret"/>. Throws <see cref="CryptographicException"/> when the key version is unknown or authentication fails.</summary>
    public string Decrypt(SealedSecret sealedSecret, Guid providerId)
    {
        ArgumentNullException.ThrowIfNull(sealedSecret);
        if (!keys.TryGetKey(sealedSecret.KeyVersion, out var key))
        {
            throw new CryptographicException($"No master key with version {sealedSecret.KeyVersion} is configured.");
        }

        if (sealedSecret.Nonce.Length != NonceBytes || sealedSecret.Tag.Length != TagBytes)
        {
            throw new CryptographicException("The stored secret is malformed.");
        }

        var data = new byte[sealedSecret.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(sealedSecret.Nonce, sealedSecret.Ciphertext, sealedSecret.Tag, data, AssociatedData(providerId, sealedSecret.KeyVersion));
            return Encoding.UTF8.GetString(data);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    private static byte[] AssociatedData(Guid providerId, short keyVersion)
    {
        var aad = new byte[18];
        providerId.TryWriteBytes(aad, bigEndian: true, out _);
        BinaryPrimitives.WriteInt16BigEndian(aad.AsSpan(16), keyVersion);
        return aad;
    }
}
