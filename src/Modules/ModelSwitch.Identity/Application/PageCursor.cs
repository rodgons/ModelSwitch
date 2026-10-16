using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace ModelSwitch.Identity.Application;

/// <summary>
/// The <c>?limit=&amp;cursor=</c> pair of every admin list (api.md section 3). A cursor is opaque to clients: the creation time
/// and id of the last item, for keyset paging newest first.
/// </summary>
internal static class PageCursor
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    /// <summary>Parses the raw query values, adding to <paramref name="errors"/> for each one that is invalid.</summary>
    public static (int Limit, (DateTimeOffset CreatedAt, Guid Id)? After) Parse(string? limit, string? cursor, Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var parsedLimit = DefaultLimit;
        if (limit is not null && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out parsedLimit) || parsedLimit is < 1 or > MaxLimit))
        {
            errors["limit"] = [$"The limit must be between 1 and {MaxLimit}."];
        }

        return (parsedLimit, ParseCursor(cursor, errors));
    }

    public static string Encode(DateTimeOffset createdAt, Guid id)
        => Base64UrlTextEncoder.Encode(Encoding.UTF8.GetBytes($"{createdAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{id}"));

    private static (DateTimeOffset, Guid)? ParseCursor(string? cursor, Dictionary<string, string[]> errors)
    {
        if (cursor is null)
        {
            return null;
        }

        try
        {
            var parts = Encoding.UTF8.GetString(Base64UrlTextEncoder.Decode(cursor)).Split('|');
            if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && Guid.TryParse(parts[1], out var id))
            {
                return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
            }
        }
        catch (FormatException)
        {
            // Falls through to the error below.
        }
        catch (ArgumentOutOfRangeException)
        {
            // Ticks outside the DateTime range.
        }

        errors["cursor"] = ["The cursor is not valid."];
        return null;
    }
}

internal sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);
