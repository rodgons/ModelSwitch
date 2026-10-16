using System.Globalization;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Pagination;

namespace ModelSwitch.Evaluation.Application;

/// <summary>Evaluation's audited actions (security.md section 3), named <c>noun.verb_past</c>.</summary>
internal static class EvaluationAuditActions
{
    public const string DatasetCreated = "dataset.created";
    public const string DatasetVersioned = "dataset.versioned";
    public const string PromptCreated = "prompt.created";
    public const string PromptVersioned = "prompt.versioned";
    public const string EvaluationCreated = "evaluation.created";
    public const string EvaluationCancelled = "evaluation.cancelled";
}

/// <summary>
/// The Tenant Grant rules every Evaluation service applies (security.md sections 3 and 4): a Tenant outside the grant, and anything
/// that belongs to one, is <c>not_found</c>, exactly like a resource that does not exist.
/// </summary>
internal sealed class EvaluationAccess(ICurrentAdministrator current, ITenantGrantChecker grants)
{
    /// <summary>The signed-in Administrator's id, recorded as <c>created_by_admin_id</c>.</summary>
    public Guid? AdministratorId => current.Current?.Id;

    public Result CheckTenant(Guid tenantId) => grants.CheckAccess(tenantId);

    /// <summary>
    /// The Tenants a list may show: the <c>tenantId</c> filter alone (404 outside the grant), else the explicit grant, else null for
    /// every Tenant. A malformed filter is <c>validation_failed</c>.
    /// </summary>
    public Result<IReadOnlyCollection<Guid>?> TenantFilter(string? tenantId)
    {
        if (current.Current is not { } administrator)
        {
            return new Error(ErrorCode.Unauthenticated, "Sign in to continue.");
        }

        if (tenantId is not null)
        {
            if (!Guid.TryParse(tenantId, CultureInfo.InvariantCulture, out var parsed))
            {
                return Error.Validation("The list query is invalid.", new Dictionary<string, string[]> { ["tenantId"] = ["The tenantId must be a UUID."] });
            }

            var access = grants.CheckAccess(administrator, parsed);
            return access.IsFailure ? access.Error : Result.Success<IReadOnlyCollection<Guid>?>(new[] { parsed });
        }

        return Result.Success<IReadOnlyCollection<Guid>?>(administrator.Grant.IsAll ? null : [.. administrator.Grant.TenantIds]);
    }

    /// <summary>
    /// The query of a Tenant-owned root list (newest first by UUIDv7 id): the Tenant filter, the page and the id the cursor points
    /// past. A Tenant outside the grant is 404; every malformed parameter is reported together as <c>validation_failed</c>.
    /// </summary>
    public Result<(IReadOnlyCollection<Guid>? Tenants, PageRequest Page, Guid? Before)> ListQuery(string? tenantId, string? limit, string? cursor)
    {
        var filter = TenantFilter(tenantId);
        if (filter.IsFailure && filter.Error.Code != ErrorCode.ValidationFailed)
        {
            return filter.Error;
        }

        var errors = new Dictionary<string, string[]>(filter.IsFailure ? filter.Error.FieldErrors! : new Dictionary<string, string[]>());
        var page = Pagination.Parse(limit, cursor);
        Guid? before = null;
        if (page.IsFailure)
        {
            foreach (var (key, messages) in page.Error.FieldErrors!)
            {
                errors[key] = messages;
            }
        }
        else if (page.Value.After is { } after)
        {
            if (Guid.TryParse(after, CultureInfo.InvariantCulture, out var parsed))
            {
                before = parsed;
            }
            else
            {
                errors["cursor"] = ["The cursor is not valid."];
            }
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The list query is invalid.", errors);
        }

        return (filter.Value, page.Value, before);
    }

    public static Error InvalidCursor()
        => Error.Validation("The pagination query is invalid.", new Dictionary<string, string[]> { ["cursor"] = ["The cursor is not valid."] });
}
