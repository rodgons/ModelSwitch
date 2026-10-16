using ModelSwitch.Identity.Contracts;

namespace ModelSwitch.Identity.Domain;

/// <summary>
/// A server-side session (ADR-0011). <see cref="Id"/> is the SHA-256 of the cookie's secret key, so a database reader cannot
/// replay a session. Expiry slides by 8 h on use but never passes the 24 h absolute limit.
/// </summary>
internal sealed class AdministratorSession
{
    public static TimeSpan SlidingLifetime => AdminAuthentication.SlidingLifetime;

    public static TimeSpan AbsoluteLifetime => AdminAuthentication.AbsoluteLifetime;

    public required string Id { get; init; }

    public Guid AdministratorId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset AbsoluteExpiresAt { get; init; }

    public static AdministratorSession Start(string id, Guid administratorId, DateTimeOffset now) => new()
    {
        Id = id,
        AdministratorId = administratorId,
        CreatedAt = now,
        ExpiresAt = now + SlidingLifetime,
        AbsoluteExpiresAt = now + AbsoluteLifetime,
    };

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt || now >= AbsoluteExpiresAt;

    /// <summary>Slides the expiry to <c>now + 8 h</c>, capped at the absolute limit.</summary>
    public void Renew(DateTimeOffset now)
    {
        var slid = now + SlidingLifetime;
        ExpiresAt = slid < AbsoluteExpiresAt ? slid : AbsoluteExpiresAt;
    }
}
