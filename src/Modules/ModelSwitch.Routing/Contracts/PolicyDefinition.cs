#pragma warning disable CS1573 // Record parameters are documented only where they need explanation.

namespace ModelSwitch.Routing.Contracts;

public enum PolicyStrategy
{
    Static,
    Rules,
    Cost,
}

/// <summary>One <c>rules[]</c> entry. A rule matches when every condition that is present holds.</summary>
/// <param name="TaskTypes">The request's Task Type must be one of these. Null: not a condition.</param>
/// <param name="Requires">The request must require every one of these Capabilities. Empty: not a condition.</param>
/// <param name="Candidates">Provider Model ids in listed order.</param>
public sealed record PolicyRule(IReadOnlyList<string>? TaskTypes, IReadOnlyList<string> Requires, IReadOnlyList<Guid> Candidates);

public sealed record BackoffSettings(int BaseMs, int MaxMs, bool FullJitter);

/// <summary>The attempt-loop bounds a Policy Version carries (routing-and-resilience.md section 4).</summary>
public sealed record ExecutionSettings(
    int DeadlineMs,
    int AttemptTimeoutMs,
    int MaxAttempts,
    int MaxRetriesPerCandidate,
    BackoffSettings Backoff)
{
    public static ExecutionSettings Default { get; } = new(60_000, 30_000, 4, 1, new BackoffSettings(200, 2_000, FullJitter: true));
}

/// <summary>A validated Routing Policy definition with Candidates resolved to Provider Model ids.</summary>
public sealed record PolicyDefinition(
    PolicyStrategy Strategy,
    IReadOnlyList<Guid> Candidates,
    IReadOnlyList<PolicyRule> Rules,
    IReadOnlyList<Guid> Default,
    IReadOnlySet<string>? AllowedProviders,
    decimal? MaxRequestCost,
    int DefaultMaxOutputTokens,
    int? MaxCandidates,
    ExecutionSettings Execution);

/// <summary>A Policy Version as the router and the attempt loop see it.</summary>
public sealed record PolicySnapshot(Guid PolicyId, string Name, int Version, PolicyDefinition Definition);
