using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ModelSwitch.Identity.Application;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Http;

namespace ModelSwitch.Identity.Infrastructure;

/// <summary>The Administrator of the current request, read from the principal the cookie scheme built from the session.</summary>
internal sealed class HttpCurrentAdministrator(IHttpContextAccessor accessor) : ICurrentAdministrator
{
    public AdministratorPrincipal? Current => AdminClaims.FromPrincipal(accessor.HttpContext?.User);
}

/// <summary>Actor, source address, request id and trace id for an Audit Record written during an HTTP request.</summary>
internal sealed class HttpAuditCallContext(IHttpContextAccessor accessor) : IAuditCallContext
{
    public AuditCallContext Capture()
    {
        var http = accessor.HttpContext;
        if (http is null)
        {
            return AuditCallContext.System;
        }

        var requestId = RequestId.Get(http);
        return new AuditCallContext(
            AdminClaims.FromPrincipal(http.User)?.Id,
            http.Connection.RemoteIpAddress,
            requestId == Guid.Empty ? null : requestId,
            Activity.Current?.TraceId.ToString());
    }
}

/// <summary>Outside an HTTP request (the Migrator, the worker) there is no actor, address or request id.</summary>
internal sealed class SystemAuditCallContext : IAuditCallContext
{
    public AuditCallContext Capture() => AuditCallContext.System;
}

/// <summary>
/// Stores the Data Protection key ring in <c>identity.data_protection_key</c>, so every api instance can read the cookies and
/// antiforgery tokens another one issued. The keys are not encrypted at rest: whoever can read this table can already read
/// the sessions (security.md section 3).
/// </summary>
internal sealed class EfXmlRepository(IDbContextFactory<IdentityDbContext> contexts, IGuidGenerator ids, TimeProvider time)
    : Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository
{
    public IReadOnlyCollection<System.Xml.Linq.XElement> GetAllElements()
    {
        using var db = contexts.CreateDbContext();
        return [.. db.DataProtectionKeys.AsNoTracking().OrderBy(k => k.CreatedAt).Select(k => k.Xml).AsEnumerable().Select(xml => System.Xml.Linq.XElement.Parse(xml))];
    }

    public void StoreElement(System.Xml.Linq.XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        using var db = contexts.CreateDbContext();
        db.DataProtectionKeys.Add(new DataProtectionKey
        {
            Id = ids.NewGuid(),
            FriendlyName = friendlyName,
            Xml = element.ToString(System.Xml.Linq.SaveOptions.DisableFormatting),
            CreatedAt = time.GetUtcNow(),
        });
        db.SaveChanges();
    }
}
