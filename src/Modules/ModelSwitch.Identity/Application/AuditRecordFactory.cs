using System.Collections;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Identity.Domain;

namespace ModelSwitch.Identity.Application;

/// <summary>Who and where an action came from, captured at the moment it is written.</summary>
internal sealed record AuditCallContext(Guid? ActorAdministratorId, IPAddress? SourceIp, Guid? RequestId, string? TraceId)
{
    public static readonly AuditCallContext System = new(null, null, null, null);
}

/// <summary>Supplies the <see cref="AuditCallContext"/> of the current HTTP request, or <see cref="AuditCallContext.System"/> outside one.</summary>
internal interface IAuditCallContext
{
    AuditCallContext Capture();
}

/// <summary>
/// Shapes an <see cref="AuditEntry"/> into an <see cref="AuditRecord"/> (data-model.md, audit_record). The details are a short
/// summary: values of sensitive-looking keys are redacted, strings are truncated and unknown value types are dropped, so a
/// careless caller still cannot persist a secret or content (ADR-0010).
/// </summary>
internal static partial class AuditRecordFactory
{
    public const string Redacted = "[redacted]";
    public const int MaxStringLength = 200;
    public const int MaxEntriesPerSection = 32;
    public const int MaxCollectionItems = 50;
    public const int MaxDetailsLength = 8 * 1024;

    // Fragments of key names that must never carry a value. "key" alone is matched exactly: key_prefix is non-secret.
    private static readonly string[] _sensitiveFragments =
    [
        "password", "secret", "token", "hash", "credential", "authorization", "cookie", "plaintext", "pepper",
        "prompt", "completion", "content", "message",
    ];

    public static AuditRecord Create(AuditEntry entry, AuditCallContext context, Guid id, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(context);
        if (!ActionPattern().IsMatch(entry.Action))
        {
            throw new ArgumentException($"Audit action '{entry.Action}' must look like 'noun.verb_past'.", nameof(entry));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(entry.TargetType);

        return new AuditRecord
        {
            Id = id,
            OccurredAt = now,
            AdministratorId = entry.ActorAdministratorId ?? context.ActorAdministratorId,
            Action = entry.Action,
            TenantId = entry.TenantId,
            TargetType = entry.TargetType,
            TargetId = entry.TargetId is { Length: > MaxStringLength } longId ? longId[..MaxStringLength] : entry.TargetId,
            Details = ShapeDetails(entry),
            SourceIp = context.SourceIp,
            RequestId = context.RequestId,
            TraceId = context.TraceId,
        };
    }

    public static string ShapeDetails(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var details = Section(entry.Details) ?? [];
        if (Section(entry.Before) is { } before)
        {
            details["before"] = before;
        }

        if (Section(entry.After) is { } after)
        {
            details["after"] = after;
        }

        var json = details.ToJsonString();
        return json.Length > MaxDetailsLength ? """{"truncated":true}""" : json;
    }

    public static bool IsSensitiveKey(string key)
    {
        var lower = key.ToLowerInvariant();
        return lower is "key" or "apikey" or "api_key" || _sensitiveFragments.Any(lower.Contains);
    }

    private static JsonObject? Section(IReadOnlyDictionary<string, object?>? values)
    {
        if (values is null)
        {
            return null;
        }

        var section = new JsonObject();
        foreach (var (key, value) in values.Take(MaxEntriesPerSection))
        {
            section[key] = IsSensitiveKey(key) ? Redacted : ToNode(value);
        }

        return section;
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        string text => Truncate(text),
        bool flag => flag,
        int number => number,
        long number => number,
        decimal number => number,
        double number => number,
        Guid guid => guid.ToString(),
        DateTimeOffset at => at.ToString("O", CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        IEnumerable items when value is not IDictionary => ToArray(items),
        _ => "[unsupported]",
    };

    private static JsonArray ToArray(IEnumerable items)
    {
        var array = new JsonArray();
        foreach (var item in items.Cast<object?>().Take(MaxCollectionItems))
        {
            array.Add(item is IEnumerable and not string ? "[unsupported]" : ToNode(item));
        }

        return array;
    }

    private static string Truncate(string text) => text.Length <= MaxStringLength ? text : text[..MaxStringLength];

    [GeneratedRegex("^[a-z][a-z0-9_]*\\.[a-z][a-z0-9_]*$")]
    private static partial Regex ActionPattern();
}
