using System.Security.Cryptography;
using System.Text;

namespace ModelSwitch.Identity.Domain;

/// <summary>HMAC-SHA256 under a server-side pepper (ADR-0006). No slow hash: the keys are 256-bit random.</summary>
internal sealed class ApiKeyHasher
{
    public const int MinimumPepperBytes = 32;

    private readonly byte[] _pepper;

    public ApiKeyHasher(byte[] pepper)
    {
        ArgumentNullException.ThrowIfNull(pepper);
        if (pepper.Length < MinimumPepperBytes)
        {
            throw new ArgumentException($"The pepper must be at least {MinimumPepperBytes} bytes.", nameof(pepper));
        }

        _pepper = pepper;
    }

    /// <summary>Returns null unless <paramref name="base64"/> decodes to at least 32 bytes.</summary>
    public static ApiKeyHasher? FromBase64(string base64)
    {
        var bytes = new byte[base64.Length];
        return Convert.TryFromBase64String(base64, bytes, out var written) && written >= MinimumPepperBytes
            ? new ApiKeyHasher(bytes[..written])
            : null;
    }

    public byte[] Hash(string fullKey) => HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes(fullKey));

    /// <summary>Constant-time comparison.</summary>
    public static bool Matches(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
        => CryptographicOperations.FixedTimeEquals(expected, actual);
}
