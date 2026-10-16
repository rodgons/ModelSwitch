namespace ModelSwitch.SharedKernel.Http;

/// <summary>One dependency whose reachability decides whether an instance should receive traffic (<c>GET /ready</c>).</summary>
public interface IReadinessCheck
{
    /// <summary>A stable lower-case name shown in the <c>/ready</c> body, e.g. <c>postgres</c>.</summary>
    string Name { get; }

    /// <summary>False when the check does not apply to this deployment (for example Valkey with <c>Limits:FailureMode=Open</c>): it is then left out of <c>/ready</c>.</summary>
    bool Applies => true;

    /// <summary>True when the dependency is reachable. Must not throw for ordinary connectivity failures.</summary>
    Task<bool> CheckAsync(CancellationToken cancellationToken);
}
