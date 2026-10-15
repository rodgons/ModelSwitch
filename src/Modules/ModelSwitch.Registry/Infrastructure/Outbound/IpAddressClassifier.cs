using System.Net;
using System.Net.Sockets;

namespace ModelSwitch.Registry.Infrastructure.Outbound;

/// <summary>Decides whether an IP address is a public unicast address a Provider may be reached at.</summary>
internal static class IpAddressClassifier
{
    private static readonly IPNetwork[] _blockedV4 =
    [
        IPNetwork.Parse("0.0.0.0/8"),        // "this" network
        IPNetwork.Parse("10.0.0.0/8"),       // RFC 1918
        IPNetwork.Parse("100.64.0.0/10"),    // CGNAT
        IPNetwork.Parse("127.0.0.0/8"),      // loopback
        IPNetwork.Parse("169.254.0.0/16"),   // link-local, incl. 169.254.169.254 metadata
        IPNetwork.Parse("172.16.0.0/12"),    // RFC 1918
        IPNetwork.Parse("192.0.0.0/24"),     // IETF protocol assignments
        IPNetwork.Parse("192.0.2.0/24"),     // TEST-NET-1
        IPNetwork.Parse("192.168.0.0/16"),   // RFC 1918
        IPNetwork.Parse("198.18.0.0/15"),    // benchmarking
        IPNetwork.Parse("198.51.100.0/24"),  // TEST-NET-2
        IPNetwork.Parse("203.0.113.0/24"),   // TEST-NET-3
        IPNetwork.Parse("224.0.0.0/4"),      // multicast
        IPNetwork.Parse("240.0.0.0/4"),      // reserved + broadcast
    ];

    private static readonly IPNetwork[] _blockedV6 =
    [
        IPNetwork.Parse("::/128"),           // unspecified
        IPNetwork.Parse("::1/128"),          // loopback
        IPNetwork.Parse("fc00::/7"),         // unique-local
        IPNetwork.Parse("fe80::/10"),        // link-local
        IPNetwork.Parse("fec0::/10"),        // deprecated site-local
        IPNetwork.Parse("ff00::/8"),         // multicast
        IPNetwork.Parse("2001:db8::/32"),    // documentation
        IPNetwork.Parse("100::/64"),         // discard-only
    ];

    /// <summary>True when the address is public unicast. IPv4-mapped, IPv4-compatible, NAT64 and 6to4 addresses are judged by the IPv4 address they embed.</summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                return IsPublic(address.MapToIPv4());
            }

            var bytes = address.GetAddressBytes();
            if (IsNat64(bytes) || (bytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0 && bytes.AsSpan(12).IndexOfAnyExcept((byte)0) >= 0))
            {
                return IsPublic(new IPAddress(bytes.AsSpan(12, 4)));
            }

            if (bytes[0] == 0x20 && bytes[1] == 0x02)
            {
                return IsPublic(new IPAddress(bytes.AsSpan(2, 4)));
            }

            return !_blockedV6.Any(n => n.Contains(address));
        }

        return address.AddressFamily == AddressFamily.InterNetwork && !_blockedV4.Any(n => n.Contains(address));
    }

    private static bool IsNat64(byte[] bytes)
        => bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b && bytes.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0;
}
