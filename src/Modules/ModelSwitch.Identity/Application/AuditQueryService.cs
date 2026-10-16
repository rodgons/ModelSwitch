using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using ModelSwitch.Identity.Domain;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Identity.Application;

/// <summary>Query-string values exactly as received; parsing and its errors belong to <see cref="AuditQueryService"/>.</summary>
internal sealed record AuditQueryRequest(string? Limit, string? Cursor, string? TenantId, string? Action, string? From, string? To);

internal sealed record AuditRecordView(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? AdministratorId,
    string Action,
    Guid? TenantId,
    string TargetType,
    string? TargetId,
    JsonNode? Details,
    string? SourceIp,
    Guid? RequestId,
    string? TraceId);

internal sealed record AuditPage(IReadOnlyList<AuditRecordView> Items, string? NextCursor);

/// <summary>Reads Audit Records for the Owner (GET /admin/audit-records). The endpoint policy is Owner, who holds every Tenant.</summary>
internal sealed class AuditQueryService(IAuditStore store)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public async Task<Result<AuditPage>> ListAsync(AuditQueryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();

        var limit = DefaultLimit;
        if (request.Limit is not null && (!int.TryParse(request.Limit, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > MaxLimit))
        {
            errors["limit"] = [$"The limit must be between 1 and {MaxLimit}."];
        }

        Guid? tenantId = null;
        if (request.TenantId is not null)
        {
            if (Guid.TryParse(request.TenantId, out var parsed))
            {
                tenantId = parsed;
            }
            else
            {
                errors["tenantId"] = ["The tenant id must be a UUID."];
            }
        }

        var from = ParseTime(request.From, "from", errors);
        var to = ParseTime(request.To, "to", errors);
        var after = ParseCursor(request.Cursor, errors);
        if (errors.Count > 0)
        {
            return Error.Validation("The audit record query is invalid.", errors);
        }

        var rows = await store.QueryAsync(new AuditQuery(limit, tenantId, request.Action, from, to, after), cancellationToken);
        var page = rows.Take(limit).ToList();
        var next = rows.Count > limit ? EncodeCursor(page[^1]) : null;
        return new AuditPage([.. page.Select(ToView)], next);
    }

    private static AuditRecordView ToView(AuditRecord record) => new(
        record.Id,
        record.OccurredAt,
        record.AdministratorId,
        record.Action,
        record.TenantId,
        record.TargetType,
        record.TargetId,
        JsonNode.Parse(record.Details),
        record.SourceIp?.ToString(),
        record.RequestId,
        record.TraceId);

    private static DateTimeOffset? ParseTime(string? value, string name, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        errors[name] = ["It must be an ISO-8601 UTC timestamp."];
        return null;
    }

    private static string EncodeCursor(AuditRecord last)
        => Base64UrlTextEncoder.Encode(Encoding.UTF8.GetBytes($"{last.OccurredAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{last.Id}"));

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
