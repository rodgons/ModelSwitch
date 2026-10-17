using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace ModelSwitch.Api.Authentication;

/// <summary>
/// Throttles repeated invalid API Keys from one client address (security.md section 2, "Abuse"). In-process and per instance,
/// best effort: after <see cref="MaxFailuresPerWindow"/> invalid keys in a fixed <see cref="Window"/>, the client is refused
/// until the window ends without the key being looked up. IPv6 clients are grouped by their /64, the smallest block one
/// subscriber usually gets, and IPv4-mapped addresses by the IPv4 address.
/// </summary>
internal sealed class ApiKeyFailureThrottle(TimeProvider time)
{
    public const int MaxFailuresPerWindow = 20;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    // Bounds memory under a spray from many addresses: expired windows are dropped once the table grows past this.
    private const int _pruneThreshold = 10_000;

    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);

    /// <summary>How long <paramref name="client"/> must wait, or null when it may present a key now.</summary>
    public TimeSpan? RetryAfter(IPAddress? client)
    {
        var now = time.GetUtcNow();
        if (!_counters.TryGetValue(Partition(client), out var counter))
        {
            return null;
        }

        lock (counter)
        {
            var ends = counter.WindowStart + Window;
            return now < ends && counter.Failures >= MaxFailuresPerWindow ? ends - now : null;
        }
    }

    public void RecordFailure(IPAddress? client)
    {
        var now = time.GetUtcNow();
        var counter = _counters.GetOrAdd(Partition(client), _ => new Counter(now));
        lock (counter)
        {
            if (now >= counter.WindowStart + Window)
            {
                counter.WindowStart = now;
                counter.Failures = 0;
            }

            counter.Failures++;
        }

        if (_counters.Count > _pruneThreshold)
        {
            Prune(now);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, counter) in _counters)
        {
            if (now >= counter.WindowStart + Window)
            {
                _counters.TryRemove(key, out _);
            }
        }
    }

    private static string Partition(IPAddress? client)
    {
        if (client is null)
        {
            return "unknown";
        }

        if (client.IsIPv4MappedToIPv6)
        {
            client = client.MapToIPv4();
        }

        if (client.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return client.ToString();
        }

        var bytes = client.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString() + "/64";
    }

    private sealed class Counter(DateTimeOffset windowStart)
    {
        public DateTimeOffset WindowStart { get; set; } = windowStart;

        public int Failures { get; set; }
    }
}
