namespace ModelSwitch.Adapters.Abstractions;

/// <summary>Result of <see cref="IProviderAdapter.CheckAsync"/>: reachable, or the classified failure.</summary>
public sealed record ConnectivityResult(bool Reachable, ProviderFailure? Failure)
{
    public static ConnectivityResult Ok { get; } = new(true, null);

    public static ConnectivityResult Unreachable(ProviderFailure failure) => new(false, failure);
}

/// <summary>A model a Provider says it serves, offered to the registration UI.</summary>
public sealed record DiscoveredModel(string Id);
