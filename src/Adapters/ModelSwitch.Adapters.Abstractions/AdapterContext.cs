namespace ModelSwitch.Adapters.Abstractions;

/// <summary>Everything an Adapter needs to call one Provider Model. <see cref="Secret"/> is resolved by the caller and never logged.</summary>
public sealed record AdapterContext(Uri Endpoint, string? Secret, string ProviderModelId, TimeSpan AttemptTimeout)
{
    public override string ToString() =>
        $"AdapterContext {{ Endpoint = {Endpoint.GetLeftPart(UriPartial.Path)}, ProviderModelId = {ProviderModelId}, AttemptTimeout = {AttemptTimeout} }}";
}
