using System.Globalization;
using System.Text.RegularExpressions;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Pagination;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Admin.Application;

/// <summary>The query string of the dashboard read endpoints exactly as received (api.md section 3, "Requests and usage").</summary>
internal sealed record DashboardQueryString
{
    public string? From { get; init; }

    public string? To { get; init; }

    public string? TenantId { get; init; }

    public string? Origin { get; init; }

    public string? Alias { get; init; }

    public string? ProviderId { get; init; }

    public string? ModelId { get; init; }

    public string? Status { get; init; }

    public string? ErrorCode { get; init; }

    public string? HadFallback { get; init; }

    public string? Sort { get; init; }

    public string? Order { get; init; }

    public string? Limit { get; init; }

    public string? Cursor { get; init; }

    /// <summary>Comma-separated or repeated: <c>tenant</c>, <c>provider</c>, <c>model</c>, <c>alias</c>.</summary>
    public IReadOnlyList<string>? GroupBy { get; init; }

    public string? Interval { get; init; }
}

/// <summary>A validated filter plus the Provider grouping that the Admin module folds itself (the rollup only knows Provider Models).</summary>
internal sealed record ParsedUsageQuery(UsageQuery Query, bool GroupByProvider, bool GroupByModel);

/// <summary>
/// Turns <see cref="DashboardQueryString"/> into the Usage module's queries. Every problem is reported at once as
/// <c>validation_failed</c> (400) with one entry per parameter. Time is <c>from</c>/<c>to</c> in ISO-8601 UTC; when absent the range
/// is the last 24 hours ending now.
/// </summary>
internal static partial class DashboardQueryParser
{
    public static readonly TimeSpan DefaultRange = TimeSpan.FromHours(24);

    /// <summary>The longest range any dashboard query may cover: the rollup's retention.</summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(400);

    /// <summary>An hourly usage report over more than this would return too many rows to be useful.</summary>
    public static readonly TimeSpan MaxHourlyRange = TimeSpan.FromDays(31);

    private static readonly string[] _statuses = ["running", "succeeded", "failed", "cancelled", "abandoned"];

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex AliasPattern();

    public static Result<RequestListQuery> Requests(DashboardQueryString query, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(query);
        var errors = new Dictionary<string, string[]>();
        var filter = Filter(query, now, errors, defaultRange: false);

        var byDuration = query.Sort switch
        {
            null or "startedAt" => false,
            "durationMs" => true,
            _ => Invalid<bool>(errors, "sort", "The sort must be startedAt or durationMs."),
        };
        var ascending = query.Order switch
        {
            null or "desc" => false,
            "asc" => true,
            _ => Invalid<bool>(errors, "order", "The order must be asc or desc."),
        };

        var page = Pagination.Parse(query.Limit, query.Cursor);
        if (page.IsFailure)
        {
            foreach (var (field, messages) in page.Error.FieldErrors!)
            {
                errors[field] = messages;
            }
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The request query is invalid.", errors);
        }

        var sort = (byDuration, ascending) switch
        {
            (false, false) => RequestSort.StartedAtDescending,
            (false, true) => RequestSort.StartedAtAscending,
            (true, false) => RequestSort.DurationDescending,
            _ => RequestSort.DurationAscending,
        };
        return new RequestListQuery(filter, sort, page.Value);
    }

    public static Result<ParsedUsageQuery> Usage(DashboardQueryString query, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(query);
        var errors = new Dictionary<string, string[]>();
        var filter = Filter(query, now, errors, defaultRange: true);

        var groups = UsageGrouping.None;
        var byProvider = false;
        var byModel = false;
        foreach (var value in (query.GroupBy ?? []).SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            switch (value)
            {
                case "tenant":
                    groups |= UsageGrouping.Tenant;
                    break;
                case "alias":
                    groups |= UsageGrouping.Alias;
                    break;
                case "model":
                    groups |= UsageGrouping.ProviderModel;
                    byModel = true;
                    break;
                case "provider":
                    // Provider Models are folded into their Provider after the query.
                    groups |= UsageGrouping.ProviderModel;
                    byProvider = true;
                    break;
                default:
                    errors["groupBy"] = ["groupBy accepts tenant, provider, model and alias."];
                    break;
            }
        }

        var interval = query.Interval switch
        {
            "hour" => UsageInterval.Hour,
            null or "day" => UsageInterval.Day,
            "month" => UsageInterval.Month,
            _ => Invalid<UsageInterval>(errors, "interval", "The interval must be hour, day or month."),
        };
        if (interval == UsageInterval.Hour && filter.To - filter.From > MaxHourlyRange)
        {
            errors["interval"] = [$"An hourly report covers at most {MaxHourlyRange.TotalDays} days."];
        }

        return errors.Count > 0
            ? Error.Validation("The usage query is invalid.", errors)
            : new ParsedUsageQuery(new UsageQuery(filter, filter.From!.Value, filter.To!.Value, groups, interval), byProvider, byModel);
    }

    public static Result<OverviewQuery> Overview(DashboardQueryString query, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(query);
        var errors = new Dictionary<string, string[]>();
        var (from, to) = Range(query, now, errors, defaultRange: true);
        var tenantId = Guid(query.TenantId, "tenantId", errors);
        return errors.Count > 0
            ? Error.Validation("The overview query is invalid.", errors)
            : new OverviewQuery(null, from!.Value, to!.Value, tenantId);
    }

    private static RequestFilter Filter(DashboardQueryString query, DateTimeOffset now, Dictionary<string, string[]> errors, bool defaultRange)
    {
        var (from, to) = Range(query, now, errors, defaultRange);
        RequestOrigin? origin = null;
        if (query.Origin is not null)
        {
            if (RequestOrigins.TryParse(query.Origin, out var parsed))
            {
                origin = parsed;
            }
            else
            {
                errors["origin"] = ["The origin must be api or evaluation."];
            }
        }

        if (query.Alias is not null && !AliasPattern().IsMatch(query.Alias))
        {
            errors["alias"] = ["The alias must match [a-z0-9][a-z0-9._-]{0,63}."];
        }

        if (query.Status is not null && !_statuses.Contains(query.Status, StringComparer.Ordinal))
        {
            errors["status"] = ["The status must be running, succeeded, failed, cancelled or abandoned."];
        }

        if (query.ErrorCode is not null && !SharedKernel.Errors.ErrorCode.All.Any(c => c.Value == query.ErrorCode))
        {
            errors["errorCode"] = ["The error code is not in the closed error-code set."];
        }

        bool? hadFallback = query.HadFallback switch
        {
            null => null,
            "true" => true,
            "false" => false,
            _ => Invalid<bool?>(errors, "hadFallback", "hadFallback must be true or false."),
        };

        return new RequestFilter(
            TenantIds: null,
            from,
            to,
            Guid(query.TenantId, "tenantId", errors),
            origin,
            query.Alias,
            Guid(query.ProviderId, "providerId", errors),
            Guid(query.ModelId, "modelId", errors),
            query.Status,
            query.ErrorCode,
            hadFallback);
    }

    // The request list has no default range (it pages back through everything retained); reports default to the last 24 hours.
    private static (DateTimeOffset? From, DateTimeOffset? To) Range(DashboardQueryString query, DateTimeOffset now, Dictionary<string, string[]> errors, bool defaultRange)
    {
        var to = Time(query.To, "to", errors) ?? (defaultRange ? now : null);
        var from = Time(query.From, "from", errors) ?? (defaultRange ? (to ?? now) - DefaultRange : null);
        if (from is { } f && to is { } t)
        {
            if (f >= t)
            {
                errors["from"] = ["from must be earlier than to."];
            }
            else if (t - f > MaxRange)
            {
                errors["from"] = [$"The range covers at most {MaxRange.TotalDays} days."];
            }
        }

        return (from, to);
    }

    private static DateTimeOffset? Time(string? value, string name, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }

        errors[name] = ["It must be an ISO-8601 UTC timestamp."];
        return null;
    }

    private static Guid? Guid(string? value, string name, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            return null;
        }

        if (System.Guid.TryParse(value, out var parsed))
        {
            return parsed;
        }

        errors[name] = ["It must be a UUID."];
        return null;
    }

    private static T Invalid<T>(Dictionary<string, string[]> errors, string name, string message)
    {
        errors[name] = [message];
        return default!;
    }
}
