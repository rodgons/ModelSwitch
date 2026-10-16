using System.Buffers.Text;
using System.Globalization;
using System.Text;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.SharedKernel.Pagination;

/// <summary>One page of an admin list: <c>{ items, nextCursor }</c> (docs/api.md section 3). A null cursor is the last page.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>A parsed page request. <see cref="After"/> is the decoded key of the last item of the previous page.</summary>
public sealed record PageRequest(int Limit, string? After);

/// <summary>
/// Cursor pagination for admin lists: <c>?limit=50&amp;cursor=&lt;opaque&gt;</c>. A cursor is the base64url of the sort key of the last
/// item returned; callers read <c>limit + 1</c> rows after it to know whether another page exists.
/// </summary>
public static class Pagination
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    /// <summary>Parses the <c>limit</c> and <c>cursor</c> query values; both problems are reported together as <c>validation_failed</c>.</summary>
    public static Result<PageRequest> Parse(string? limit, string? cursor)
    {
        var errors = new Dictionary<string, string[]>();
        var size = DefaultLimit;
        if (limit is not null && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out size) || size is < 1 or > MaxLimit))
        {
            errors["limit"] = [$"The limit must be between 1 and {MaxLimit}."];
        }

        string? after = null;
        if (cursor is not null && !TryDecode(cursor, out after))
        {
            errors["cursor"] = ["The cursor is not valid."];
        }

        return errors.Count > 0
            ? Error.Validation("The pagination query is invalid.", errors)
            : new PageRequest(size, after);
    }

    /// <summary>Builds the page from up to <c>limit + 1</c> rows already in sort order; <paramref name="keyOf"/> yields the cursor key of a row.</summary>
    public static PagedResult<T> Page<T>(IReadOnlyList<T> rows, int limit, Func<T, string> keyOf)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(keyOf);
        var items = rows.Take(limit).ToList();
        return new PagedResult<T>(items, rows.Count > limit ? Encode(keyOf(items[^1])) : null);
    }

    public static string Encode(string key) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(key));

    public static bool TryDecode(string cursor, out string key)
    {
        key = string.Empty;
        try
        {
            key = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
            return key.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
