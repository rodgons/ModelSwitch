using Microsoft.Extensions.Configuration;

namespace ModelSwitch.Registry.Infrastructure.Outbound;

/// <summary>Deployment settings under <c>Outbound</c> (<c>MODELSWITCH__Outbound__AllowedPrivateHosts</c>, comma separated).</summary>
internal sealed class OutboundOptions
{
    public const string AllowedPrivateHostsKey = "Outbound:AllowedPrivateHosts";

    public OutboundOptions(IEnumerable<string> allowedPrivateHosts)
    {
        ArgumentNullException.ThrowIfNull(allowedPrivateHosts);
        AllowedPrivateHosts = new HashSet<string>(
            allowedPrivateHosts.Select(Normalize).Where(h => h.Length > 0),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> AllowedPrivateHosts { get; }

    public static OutboundOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new OutboundOptions((configuration[AllowedPrivateHostsKey] ?? string.Empty).Split(','));
    }

    public bool IsAllowed(string host) => AllowedPrivateHosts.Contains(Normalize(host));

    private static string Normalize(string host) => host.Trim().Trim('[', ']').TrimEnd('.');
}
