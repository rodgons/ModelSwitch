using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using ModelSwitch.SharedKernel.Hosting;

namespace ModelSwitch.Registry.Infrastructure.Secrets;

/// <summary>
/// The master keys (ADR-0007). <c>Security:MasterKey</c> (with <c>Security:MasterKeyVersion</c>, default 1) encrypts new
/// secrets. To rotate, set a new current key with a higher version and move the old one to
/// <c>Security:PreviousMasterKey</c> / <c>Security:PreviousMasterKeyVersion</c>; rows written under it still decrypt
/// until <c>ReencryptAllAsync</c> rewrites them. Each key is 32 bytes, base64.
/// </summary>
internal sealed class MasterKeyRing
{
    public const string KeySetting = "Security:MasterKey";
    public const string VersionSetting = "Security:MasterKeyVersion";
    public const string PreviousKeySetting = "Security:PreviousMasterKey";
    public const string PreviousVersionSetting = "Security:PreviousMasterKeyVersion";
    public const int KeyBytes = 32;

    private readonly Dictionary<short, byte[]> _keys;

    private MasterKeyRing(short currentVersion, Dictionary<short, byte[]> keys)
    {
        CurrentVersion = currentVersion;
        _keys = keys;
    }

    /// <summary>A ring with no keys: no <c>db:</c> secret can be written or read.</summary>
    public static MasterKeyRing Empty { get; } = new(0, []);

    public bool HasKey => _keys.Count > 0;

    public short CurrentVersion { get; }

    public static MasterKeyRing Create(short currentVersion, byte[] currentKey, short? previousVersion = null, byte[]? previousKey = null)
    {
        ArgumentNullException.ThrowIfNull(currentKey);
        Dictionary<short, byte[]> keys = new() { [currentVersion] = currentKey };
        if (previousVersion is { } version && previousKey is not null)
        {
            keys[version] = previousKey;
        }

        return new MasterKeyRing(currentVersion, keys);
    }

    /// <summary>Reads the ring from configuration. Returns the problems found (each naming its environment variable); an unset key is not a problem here.</summary>
    public static (MasterKeyRing Ring, IReadOnlyList<string> Problems) From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        List<string> problems = [];
        var current = ReadKey(configuration, KeySetting, VersionSetting, 1, problems);
        var previous = ReadKey(configuration, PreviousKeySetting, PreviousVersionSetting, null, problems);
        if (previous is not null && current is null && problems.Count == 0)
        {
            problems.Add($"{Variable(PreviousKeySetting)} is set but {Variable(KeySetting)} is not.");
        }

        if (previous is not null && current is not null && previous.Value.Version == current.Value.Version)
        {
            problems.Add($"{Variable(PreviousVersionSetting)} must differ from {Variable(VersionSetting)}.");
        }

        return current is null || problems.Count > 0
            ? (Empty, problems)
            : (Create(current.Value.Version, current.Value.Key, previous?.Version, previous?.Key), problems);
    }

    public bool TryGetKey(short version, out byte[] key)
    {
        var found = _keys.TryGetValue(version, out var value);
        key = value ?? [];
        return found;
    }

    private static (short Version, byte[] Key)? ReadKey(IConfiguration configuration, string keySetting, string versionSetting, short? defaultVersion, List<string> problems)
    {
        var text = configuration[keySetting];
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var versionText = configuration[versionSetting];
        short version;
        if (string.IsNullOrWhiteSpace(versionText))
        {
            if (defaultVersion is null)
            {
                problems.Add($"{Variable(versionSetting)} is required when {Variable(keySetting)} is set.");
                return null;
            }

            version = defaultVersion.Value;
        }
        else if (!short.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out version) || version < 1)
        {
            problems.Add($"{Variable(versionSetting)} is invalid: expected an integer from 1 to {short.MaxValue}.");
            return null;
        }

        var bytes = new byte[KeyBytes + 1];
        if (!Convert.TryFromBase64String(text.Trim(), bytes, out var written) || written != KeyBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            problems.Add($"{Variable(keySetting)} is invalid: expected {KeyBytes} bytes, base64 encoded.");
            return null;
        }

        return (version, bytes[..KeyBytes]);
    }

    private static string Variable(string setting) => RequiredSetting.EnvironmentVariableFor(setting);
}
