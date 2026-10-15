namespace ModelSwitch.Registry.Contracts;

/// <summary>
/// Creates the primary handler for Adapter <c>HttpClient</c>s: every connection is checked against the outbound
/// destination policy, redirects and proxies are off, and connections are recycled every 5 minutes.
/// No resilience handlers are added (ADR-0004).
/// </summary>
public interface IOutboundHandlerFactory
{
    HttpMessageHandler CreatePrimaryHandler();
}
