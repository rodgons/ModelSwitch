using ModelSwitch.Identity.Contracts;
using ModelSwitch.Identity.Domain;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Identity.Application;

internal sealed record CreateTenantRequest(string? Name);

/// <summary>A null field is left unchanged.</summary>
internal sealed record UpdateTenantRequest(string? Name, string? Status);

internal sealed record BudgetRequest(decimal? Amount, string? Period);

/// <summary>PUT semantics: the body replaces all limits, and a missing or null limit means unlimited.</summary>
internal sealed record UpdateLimitsRequest(int? RequestsPerMinute, int? MaxConcurrent, decimal? MaxRequestCost, BudgetRequest? Budget);

internal sealed record UpdateModelAccessRequest(IReadOnlyList<string?>? AllowedAliases);

internal sealed record TenantView(Guid Id, string Name, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static TenantView From(Tenant tenant) => new(tenant.Id, tenant.Name, TenantRules.ToWire(tenant.Status), tenant.CreatedAt, tenant.UpdatedAt);
}

internal sealed record BudgetView(decimal Amount, string Period, string Currency);

internal sealed record LimitsView(int? RequestsPerMinute, int? MaxConcurrent, decimal? MaxRequestCost, BudgetView? Budget);

internal sealed record ModelAccessView(IReadOnlyList<string> AllowedAliases);

/// <summary>The current month of the Tenant's Budget: <c>spent + reserved</c> is what the next Reservation is checked against.</summary>
internal sealed record BudgetPeriodView(DateOnly PeriodStart, decimal? Limit, decimal Reserved, decimal Spent, string Currency);

internal sealed record TenantDetailView(
    Guid Id,
    string Name,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    LimitsView Limits,
    ModelAccessView ModelAccess,
    BudgetPeriodView BudgetPeriod);

/// <summary>
/// Tenants, their limits and their model access (api.md section 3). The endpoint policy enforces the Role; every method here checks
/// the Tenant Grant, so a Tenant outside it answers 404. Writes are audited and bump the configuration version in the same
/// transaction (the store's contract).
/// </summary>
internal sealed class TenantService(
    ITenantAdminStore store,
    ITenantBudgets budgets,
    ICurrentAdministrator current,
    ITenantGrantChecker grants,
    VerifiedKeyCache keyCache,
    IGuidGenerator ids,
    TimeProvider time)
{
    private static readonly Error _notFound = new(ErrorCode.NotFound, "The tenant was not found.");

    public async Task<Result<Page<TenantView>>> ListAsync(string? limit, string? cursor, CancellationToken cancellationToken)
    {
        if (current.Current is not { } administrator)
        {
            return new Error(ErrorCode.Unauthenticated, "Sign in to continue.");
        }

        var errors = new Dictionary<string, string[]>();
        var (pageSize, after) = PageCursor.Parse(limit, cursor, errors);
        if (errors.Count > 0)
        {
            return Error.Validation("The tenant list query is invalid.", errors);
        }

        var rows = await store.ListAsync(administrator.Grant, pageSize, after, cancellationToken);
        var page = rows.Take(pageSize).ToList();
        return new Page<TenantView>([.. page.Select(TenantView.From)], rows.Count > pageSize ? PageCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null);
    }

    public async Task<Result<TenantDetailView>> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var access = grants.CheckAccess(tenantId);
        if (access.IsFailure)
        {
            return access.Error;
        }

        var tenant = await store.FindAsync(tenantId, cancellationToken);
        return tenant is null ? _notFound : await DetailAsync(tenant, cancellationToken);
    }

    public async Task<Result<TenantView>> CreateAsync(CreateTenantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (current.Current is not { } administrator)
        {
            return new Error(ErrorCode.Unauthenticated, "Sign in to continue.");
        }

        // A new Tenant is outside any explicit grant, so only an all-Tenants Administrator may create one.
        if (!administrator.Grant.IsAll)
        {
            return new Error(ErrorCode.Forbidden, "Creating a tenant needs an all-tenants grant.");
        }

        var errors = new Dictionary<string, string[]>();
        var name = TenantRules.ValidateName(request.Name, errors);
        if (errors.Count > 0)
        {
            return Error.Validation("The tenant is invalid.", errors);
        }

        var now = time.GetUtcNow();
        var tenant = new Tenant { Id = ids.NewGuid(), Name = name!, Status = TenantStatus.Active, CreatedAt = now, UpdatedAt = now };
        var audit = new AuditEntry(AuditActions.TenantCreated, "tenant", tenant.Id.ToString())
        {
            TenantId = tenant.Id,
            After = new Dictionary<string, object?> { ["name"] = tenant.Name, ["status"] = TenantRules.ToWire(tenant.Status) },
        };
        return await store.AddAsync(tenant, audit, cancellationToken)
            ? TenantView.From(tenant)
            : new Error(ErrorCode.Conflict, "A tenant with this name already exists.");
    }

    public async Task<Result<TenantView>> UpdateAsync(Guid tenantId, UpdateTenantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var access = grants.CheckAccess(tenantId);
        if (access.IsFailure)
        {
            return access.Error;
        }

        var tenant = await store.FindAsync(tenantId, cancellationToken);
        if (tenant is null)
        {
            return _notFound;
        }

        var errors = new Dictionary<string, string[]>();
        var name = request.Name is null ? tenant.Name : TenantRules.ValidateName(request.Name, errors);
        var status = request.Status is null ? tenant.Status : TenantRules.ValidateStatus(request.Status, errors);
        if (errors.Count > 0)
        {
            return Error.Validation("The tenant is invalid.", errors);
        }

        Dictionary<string, object?> before = [];
        Dictionary<string, object?> after = [];
        if (name != tenant.Name)
        {
            before["name"] = tenant.Name;
            after["name"] = name;
        }

        if (status != tenant.Status)
        {
            before["status"] = TenantRules.ToWire(tenant.Status);
            after["status"] = TenantRules.ToWire(status!.Value);
        }

        if (after.Count == 0)
        {
            return TenantView.From(tenant);
        }

        var action = !after.ContainsKey("status")
            ? AuditActions.TenantModified
            : status == TenantStatus.Suspended ? AuditActions.TenantSuspended : AuditActions.TenantReactivated;
        tenant.Name = name!;
        tenant.Status = status!.Value;
        tenant.UpdatedAt = time.GetUtcNow();
        var audit = new AuditEntry(action, "tenant", tenantId.ToString()) { TenantId = tenantId, Before = before, After = after };
        if (!await store.SaveAsync(tenant, audit, cancellationToken))
        {
            return new Error(ErrorCode.Conflict, "A tenant with this name already exists.");
        }

        // This instance stops serving cached keys of a (now) suspended Tenant at once; the others within the cache's bound.
        keyCache.Clear();
        return TenantView.From(tenant);
    }

    public async Task<Result<LimitsView>> SetLimitsAsync(Guid tenantId, UpdateLimitsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var access = grants.CheckAccess(tenantId);
        if (access.IsFailure)
        {
            return access.Error;
        }

        if (await store.FindAsync(tenantId, cancellationToken) is null)
        {
            return _notFound;
        }

        var errors = new Dictionary<string, string[]>();
        var requestsPerMinute = TenantRules.ValidateLimit(request.RequestsPerMinute, "requestsPerMinute", errors);
        var maxConcurrent = TenantRules.ValidateLimit(request.MaxConcurrent, "maxConcurrent", errors);
        var maxRequestCost = TenantRules.ValidateMoney(request.MaxRequestCost, "maxRequestCost", errors);
        var budget = TenantRules.ValidateBudget(request.Budget, errors);
        if (errors.Count > 0)
        {
            return Error.Validation("The limits are invalid.", errors);
        }

        var beforeRow = await store.FindLimitsAsync(tenantId, cancellationToken);
        var beforeBudget = await budgets.GetMonthlyLimitAsync(tenantId, cancellationToken);
        var audit = new AuditEntry(AuditActions.TenantLimitsChanged, "tenant", tenantId.ToString())
        {
            TenantId = tenantId,
            Before = SummariseLimits(beforeRow?.RequestsPerMinute, beforeRow?.MaxConcurrent, beforeRow?.MaxRequestCost, beforeBudget),
            After = SummariseLimits(requestsPerMinute, maxConcurrent, maxRequestCost, budget),
        };
        await store.SaveLimitsAsync(new TenantLimitsChange(tenantId, requestsPerMinute, maxConcurrent, maxRequestCost, budget, time.GetUtcNow()), audit, cancellationToken);
        return ToView(requestsPerMinute, maxConcurrent, maxRequestCost, budget);
    }

    public async Task<Result<ModelAccessView>> SetModelAccessAsync(Guid tenantId, UpdateModelAccessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var access = grants.CheckAccess(tenantId);
        if (access.IsFailure)
        {
            return access.Error;
        }

        if (await store.FindAsync(tenantId, cancellationToken) is null)
        {
            return _notFound;
        }

        var errors = new Dictionary<string, string[]>();
        var aliases = TenantRules.ValidateAliases(request.AllowedAliases, errors);
        if (errors.Count > 0)
        {
            return Error.Validation("The model access is invalid.", errors);
        }

        var before = (await store.FindLimitsAsync(tenantId, cancellationToken))?.AllowedAliases ?? [TenantRules.Wildcard];
        var audit = new AuditEntry(AuditActions.TenantModelAccessChanged, "tenant", tenantId.ToString())
        {
            TenantId = tenantId,
            Before = new Dictionary<string, object?> { ["allowedAliases"] = before },
            After = new Dictionary<string, object?> { ["allowedAliases"] = aliases },
        };
        await store.SaveAllowedAliasesAsync(tenantId, aliases!, time.GetUtcNow(), audit, cancellationToken);
        return new ModelAccessView(aliases!);
    }

    private async Task<TenantDetailView> DetailAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        var row = await store.FindLimitsAsync(tenant.Id, cancellationToken);
        var budget = await budgets.GetMonthlyLimitAsync(tenant.Id, cancellationToken);
        var period = await budgets.GetCurrentPeriodAsync(tenant.Id, cancellationToken);
        return new TenantDetailView(
            tenant.Id,
            tenant.Name,
            TenantRules.ToWire(tenant.Status),
            tenant.CreatedAt,
            tenant.UpdatedAt,
            ToView(row?.RequestsPerMinute, row?.MaxConcurrent, row?.MaxRequestCost, budget),
            new ModelAccessView(row?.AllowedAliases ?? [TenantRules.Wildcard]),
            new BudgetPeriodView(period.PeriodStart, period.Limit, period.Reserved, period.Spent, Money.Usd));
    }

    private static LimitsView ToView(int? requestsPerMinute, int? maxConcurrent, decimal? maxRequestCost, decimal? budget)
        => new(requestsPerMinute, maxConcurrent, maxRequestCost, budget is { } amount ? new BudgetView(amount, TenantRules.BudgetPeriodMonth, Money.Usd) : null);

    private static Dictionary<string, object?> SummariseLimits(int? requestsPerMinute, int? maxConcurrent, decimal? maxRequestCost, decimal? budget) => new()
    {
        ["requestsPerMinute"] = requestsPerMinute,
        ["maxConcurrent"] = maxConcurrent,
        ["maxRequestCost"] = maxRequestCost,
        ["budgetMonthly"] = budget,
    };
}
