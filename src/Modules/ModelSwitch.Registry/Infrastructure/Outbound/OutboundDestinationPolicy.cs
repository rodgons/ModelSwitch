using System.Net;
using System.Net.Sockets;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Registry.Infrastructure.Outbound;

internal sealed class OutboundDestinationPolicy(OutboundOptions options, IDnsResolver resolver) : IOutboundDestinationPolicy
{
    public async ValueTask<Result> ValidateEndpointAsync(string endpoint, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri))
        {
            return Invalid("Endpoint must be an absolute URL.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return Invalid("Endpoint scheme must be https (or http for an allowed private host).");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return Invalid("Endpoint must not contain credentials.");
        }

        var host = uri.DnsSafeHost;
        var allowed = options.IsAllowed(host);
        if (uri.Scheme == Uri.UriSchemeHttp && !allowed)
        {
            return Invalid("Endpoint must use https unless its host is in the allowed private hosts.");
        }

        if (allowed)
        {
            return Result.Success();
        }

        var addresses = await ResolveAndCheckAsync(host, resolver, cancellationToken).ConfigureAwait(false);
        return addresses.IsSuccess ? Result.Success() : Invalid(addresses.Error.Message);
    }

    /// <summary>Resolves <paramref name="host"/> (or parses an IP literal) and requires every address to be public.</summary>
    internal static async ValueTask<Result<IReadOnlyList<IPAddress>>> ResolveAndCheckAsync(
        string host, IDnsResolver resolver, CancellationToken cancellationToken)
    {
        IReadOnlyList<IPAddress> addresses;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return new Error(ErrorCode.ValidationFailed, "Endpoint host could not be resolved.");
            }
        }

        if (addresses.Count == 0)
        {
            return new Error(ErrorCode.ValidationFailed, "Endpoint host could not be resolved.");
        }

        return addresses.All(IpAddressClassifier.IsPublic)
            ? Result.Success(addresses)
            : new Error(ErrorCode.ValidationFailed, "Endpoint host resolves to a private, loopback, link-local or otherwise non-public address.");
    }

    private static Error Invalid(string message)
        => Error.Validation(message, new Dictionary<string, string[]> { ["endpoint"] = [message] });
}
