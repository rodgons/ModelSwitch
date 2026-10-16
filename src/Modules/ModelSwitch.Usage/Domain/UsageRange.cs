namespace ModelSwitch.Usage.Domain;

/// <summary>Which slices of a time range are read raw and which from <c>usage_hourly</c>.</summary>
/// <param name="Exact">True when the whole range is read raw, so percentiles can be exact (<c>percentile_cont</c>).</param>
/// <param name="Hourly">The whole hours read from the rollup, or null.</param>
/// <param name="Raw">The slices read from <c>inference_request</c>.</param>
internal sealed record UsageRangePlan(bool Exact, (DateTimeOffset From, DateTimeOffset To)? Hourly, IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> Raw);

/// <summary>
/// The reading rule of usage-and-budgets.md section 6. Ranges up to <see cref="ExactLimit"/> read the raw request rows (a day of
/// requests is small and percentiles are then exact). Longer ranges read <c>usage_hourly</c> for whole hours and the raw rows for
/// the partial hours at each edge, which also covers requests the rollup has not caught up with in the current hour.
/// </summary>
internal static class UsageRange
{
    public static readonly TimeSpan ExactLimit = TimeSpan.FromHours(24);

    public static UsageRangePlan Plan(DateTimeOffset from, DateTimeOffset to)
    {
        if (to - from <= ExactLimit)
        {
            return new UsageRangePlan(true, null, [(from, to)]);
        }

        var firstHour = CeilingHour(from);
        var lastHour = FloorHour(to);
        var raw = new List<(DateTimeOffset, DateTimeOffset)>(2);
        if (from < firstHour)
        {
            raw.Add((from, firstHour));
        }

        if (lastHour < to)
        {
            raw.Add((lastHour, to));
        }

        return new UsageRangePlan(false, (firstHour, lastHour), raw);
    }

    /// <summary>The time-series step for a range: about 12 to 170 points, never finer than the rollup can serve beyond a day.</summary>
    public static TimeSpan StepFor(TimeSpan range)
        => range <= TimeSpan.FromHours(1) ? TimeSpan.FromMinutes(5)
            : range <= TimeSpan.FromHours(6) ? TimeSpan.FromMinutes(15)
            : range <= TimeSpan.FromDays(7) ? TimeSpan.FromHours(1)
            : TimeSpan.FromDays(1);

    /// <summary>The start of the step-aligned bucket (UTC, from the Unix epoch) that holds <paramref name="at"/>.</summary>
    public static DateTimeOffset BucketOf(DateTimeOffset at, TimeSpan step)
    {
        var ticks = at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        return DateTimeOffset.UnixEpoch.AddTicks(ticks - (ticks % step.Ticks));
    }

    private static DateTimeOffset FloorHour(DateTimeOffset at) => BucketOf(at, TimeSpan.FromHours(1));

    private static DateTimeOffset CeilingHour(DateTimeOffset at)
    {
        var floor = FloorHour(at);
        return floor == at ? at : floor.AddHours(1);
    }
}
