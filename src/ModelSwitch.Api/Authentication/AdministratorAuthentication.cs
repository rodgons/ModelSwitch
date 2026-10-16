using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Http;

namespace ModelSwitch.Api.Authentication;

/// <summary>Cookie authentication and Role policies for <c>/admin/*</c> (ADR-0011). The API Key scheme keeps <c>/v1/*</c>.</summary>
internal static class AdministratorAuthentication
{
    /// <summary>Adds the <c>Administrator</c> cookie scheme: a server-side session behind an opaque, <c>__Host-</c> prefixed cookie.</summary>
    public static AuthenticationBuilder AddAdministratorCookie(this AuthenticationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<SessionTicketStore>();
        builder.Services.AddOptions<CookieAuthenticationOptions>(AdminAuthentication.Scheme)
            .Configure<SessionTicketStore, TimeProvider>((options, store, time) =>
            {
                options.SessionStore = store;
                options.TimeProvider = time;
            });

        return builder.AddCookie(AdminAuthentication.Scheme, options =>
        {
            options.Cookie.Name = AdminAuthentication.CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
            options.ExpireTimeSpan = AdminAuthentication.SlidingLifetime;
            options.SlidingExpiration = true;

            // An API never redirects to a login page: answer with Problem Details (api.md section 3).
            options.Events.OnRedirectToLogin = context => ProblemResults
                .From(context.HttpContext, new Error(ErrorCode.Unauthenticated, "Sign in to continue."))
                .ExecuteAsync(context.HttpContext);
            options.Events.OnRedirectToAccessDenied = context => ProblemResults
                .From(context.HttpContext, new Error(ErrorCode.Forbidden, "Your role does not allow this action."))
                .ExecuteAsync(context.HttpContext);
        });
    }

    /// <summary>One policy per Role. A Role satisfies every policy at or below it, so an Owner passes all three.</summary>
    public static AuthorizationBuilder AddAdministratorPolicies(this AuthorizationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var role in Enum.GetValues<AdministratorRole>())
        {
            builder.AddPolicy(AdminPolicies.For(role), policy => policy
                .AddAuthenticationSchemes(AdminAuthentication.Scheme)
                .RequireClaim(AdminClaims.Role, AdministratorRoles.AtLeast(role)));
        }

        return builder;
    }

    /// <summary>Rejects an unsafe <c>/admin/*</c> request that lacks a valid antiforgery token with a 400 <c>validation_failed</c>.</summary>
    public static IApplicationBuilder UseAdminAntiforgery(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<AdminAntiforgeryMiddleware>();
    }
}

internal sealed class AdminAntiforgeryMiddleware(RequestDelegate next, IAntiforgery antiforgery)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (RequiresToken(context.Request))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                var error = Error.Validation(
                    "The antiforgery token is missing or invalid.",
                    new Dictionary<string, string[]> { [AdminAuthentication.AntiforgeryHeaderName] = ["A valid antiforgery token is required."] });
                await ProblemResults.From(context, error).ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }

    // Every non-GET admin request (api.md section 3).
    internal static bool RequiresToken(HttpRequest request)
        => request.Path.StartsWithSegments(AdminAuthentication.AdminPathPrefix, StringComparison.OrdinalIgnoreCase)
            && !HttpMethods.IsGet(request.Method)
            && !HttpMethods.IsHead(request.Method)
            && !HttpMethods.IsOptions(request.Method)
            && !HttpMethods.IsTrace(request.Method);
}

/// <summary>
/// Keeps the cookie free of the ticket: the cookie carries only the session key, and <see cref="IAdministratorSessions"/>
/// (Identity) owns the rows. The principal is rebuilt from the database on every request, so a Role change or a deactivation
/// applies immediately and sign-out revokes at once.
/// </summary>
internal sealed class SessionTicketStore(IAdministratorSessions sessions) : ITicketStore
{
    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var administrator = AdminClaims.FromPrincipal(ticket.Principal)
            ?? throw new InvalidOperationException("Only an Administrator principal can start a session.");
        var session = await sessions.CreateAsync(administrator.Id, CancellationToken.None);
        ticket.Properties.IssuedUtc = session.IssuedAt;
        ticket.Properties.ExpiresUtc = session.ExpiresAt;
        return session.SessionKey;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var renewed = await sessions.RenewAsync(key, CancellationToken.None);
        if (renewed is not null)
        {
            ticket.Properties.ExpiresUtc = renewed.ExpiresAt;
        }
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var session = await sessions.ResolveAsync(key, CancellationToken.None);
        if (session is null)
        {
            return null;
        }

        var properties = new AuthenticationProperties { IssuedUtc = session.IssuedAt, ExpiresUtc = session.ExpiresAt };
        return new AuthenticationTicket(AdminClaims.ToPrincipal(session.Administrator), properties, AdminAuthentication.Scheme);
    }

    public Task RemoveAsync(string key) => sessions.RevokeAsync(key, CancellationToken.None);
}
