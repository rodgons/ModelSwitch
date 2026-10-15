using Microsoft.AspNetCore.Http;

namespace ModelSwitch.SharedKernel.Http;

/// <summary>
/// The Inference Request ID of the current HTTP request: a UUIDv7 assigned by <see cref="RequestIdMiddleware"/> and
/// returned in <c>X-Request-Id</c> on every response (docs/api.md section 1).
/// </summary>
public static class RequestId
{
    public const string HeaderName = "X-Request-Id";

    private const string _itemKey = "ModelSwitch.RequestId";

    /// <summary>The request's id, or <see cref="Guid.Empty"/> when <see cref="RequestIdMiddleware"/> did not run.</summary>
    public static Guid Get(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(_itemKey, out var value) && value is Guid id ? id : Guid.Empty;
    }

    internal static void Set(HttpContext context, Guid id) => context.Items[_itemKey] = id;
}

/// <summary>Assigns a UUIDv7 request id and puts it on the response before anything else can write one.</summary>
public sealed class RequestIdMiddleware(RequestDelegate next, IGuidGenerator ids)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var id = ids.NewGuid();
        RequestId.Set(context, id);
        context.Response.Headers[RequestId.HeaderName] = id.ToString();
        return next(context);
    }
}
