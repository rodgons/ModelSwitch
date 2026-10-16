using System.Collections;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelSwitch.SharedKernel.Telemetry;

namespace ModelSwitch.SharedKernel.Http;

/// <summary>
/// The Inference Request ID of the current HTTP request: a UUIDv7 assigned by <see cref="RequestIdMiddleware"/> and
/// returned in <c>X-Request-Id</c> on every response (docs/api.md section 1).
/// </summary>
public static class RequestId
{
    public const string HeaderName = "X-Request-Id";

    private const string _itemKey = "ModelSwitch.RequestId";
    private const string _scopeKey = "ModelSwitch.LogScope";

    /// <summary>The request's id, or <see cref="Guid.Empty"/> when <see cref="RequestIdMiddleware"/> did not run.</summary>
    public static Guid Get(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(_itemKey, out var value) && value is Guid id ? id : Guid.Empty;
    }

    /// <summary>
    /// Adds the Tenant to every later log line of this request (its logging scope) and to the server span. An id, never a name
    /// (observability.md section 4).
    /// </summary>
    public static void SetTenant(HttpContext context, Guid tenantId)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Items.TryGetValue(_scopeKey, out var value) && value is RequestLogScope scope)
        {
            scope.TenantId = tenantId;
        }

        Activity.Current?.SetTag(GatewayTelemetry.Attributes.TenantId, tenantId.ToString());
    }

    internal static void Set(HttpContext context, Guid id) => context.Items[_itemKey] = id;

    internal static void SetScope(HttpContext context, RequestLogScope scope) => context.Items[_scopeKey] = scope;
}

/// <summary>Assigns a UUIDv7 request id, puts it on the response before anything else can write one, and opens the request's logging scope.</summary>
public sealed class RequestIdMiddleware(RequestDelegate next, IGuidGenerator ids, ILogger<RequestIdMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var id = ids.NewGuid();
        RequestId.Set(context, id);
        context.Response.Headers[RequestId.HeaderName] = id.ToString();
        Activity.Current?.SetTag(GatewayTelemetry.Attributes.RequestId, id.ToString());

        var scope = new RequestLogScope(id);
        RequestId.SetScope(context, scope);
        using (logger.BeginScope(scope))
        {
            await next(context);
        }
    }
}

/// <summary>
/// The logging scope of one HTTP request: <c>RequestId</c>, then <c>TenantId</c> once authentication has found it. The scope is read
/// each time a line is written, so setting the Tenant later still reaches every line after it.
/// </summary>
internal sealed class RequestLogScope(Guid requestId) : IReadOnlyList<KeyValuePair<string, object?>>
{
    public Guid? TenantId { get; set; }

    public int Count => TenantId is null ? 1 : 2;

    public KeyValuePair<string, object?> this[int index] => index switch
    {
        0 => new("RequestId", requestId),
        1 when TenantId is { } tenant => new("TenantId", tenant),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => TenantId is { } tenant ? $"RequestId:{requestId} TenantId:{tenant}" : $"RequestId:{requestId}";
}
