using System.Buffers.Text;
using System.Security.Cryptography;

namespace ModelSwitch.Identity.Domain;

/// <summary>
/// The API Key wire format: <c>msk_</c> + 8-character Key Prefix + <c>_</c> + 43-character base64url secret
/// (32 random bytes). docs/security.md section 2.
/// </summary>
internal static class ApiKeyFormat
{
    public const string Marker = "msk_";
    public const int PrefixLength = 8;
    public const int SecretBytes = 32;
    public const int SecretLength = 43;
    public const int TotalLength = 4 + PrefixLength + 1 + SecretLength;

    /// <summary>Lower-case base32 (Crockford alphabet: no i, l, o or u), so prefixes are easy to read aloud.</summary>
    public const string PrefixAlphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    public static GeneratedApiKey Generate()
    {
        var prefix = new string(RandomNumberGenerator.GetItems<char>(PrefixAlphabet, PrefixLength));
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes));
        return new GeneratedApiKey(prefix, $"{Marker}{prefix}_{secret}");
    }

    /// <summary>Checks shape only. A well-formed key can still be unknown, revoked or expired.</summary>
    public static bool TryParse(string? value, out ParsedApiKey parsed)
    {
        parsed = default;
        if (value is null || value.Length != TotalLength || !value.StartsWith(Marker, StringComparison.Ordinal))
        {
            return false;
        }

        if (value[Marker.Length + PrefixLength] != '_')
        {
            return false;
        }

        var prefix = value.AsSpan(Marker.Length, PrefixLength);
        foreach (var c in prefix)
        {
            if (!PrefixAlphabet.Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        var secret = value.AsSpan(Marker.Length + PrefixLength + 1);
        foreach (var c in secret)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        parsed = new ParsedApiKey(new string(prefix));
        return true;
    }
}

internal readonly record struct ParsedApiKey(string Prefix);

internal sealed record GeneratedApiKey(string Prefix, string Plaintext);
