using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Routing.Domain;
using ModelSwitch.Routing.Infrastructure;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Routing.Application;

internal sealed record BindAliasRequest(Guid? PolicyId, string? Description);

internal sealed record AliasView(string Alias, Guid PolicyId, string PolicyName, string? Description, DateTimeOffset UpdatedAt);

/// <summary>An alias binding result: the view and whether the alias was newly created (201) or rebound (200).</summary>
internal sealed record AliasBinding(AliasView View, bool Created);

/// <summary>Model Alias binding (ADR-0008; Owner writes). An alias always resolves to its policy's latest version.</summary>
internal sealed partial class AliasAdminService(
    IDbContextFactory<RoutingDbContext> contexts,
    IAuditWriter audit,
    TimeProvider time)
{
    private const int _maxDescriptionLength = 500;

    public async Task<Result<AliasView>> GetAsync(string alias, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await (
            from a in db.Aliases.AsNoTracking()
            join p in db.Policies.AsNoTracking() on a.PolicyId equals p.Id
            where a.Alias == alias
            select new AliasView(a.Alias, a.PolicyId, p.Name, a.Description, a.UpdatedAt))
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? NotFound() : row;
    }

    public async Task<Result<AliasBinding>> SaveAsync(string alias, BindAliasRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        if (!IsValidAlias(alias))
        {
            errors["alias"] = ["The alias must be 1 to 64 characters: lower-case letters, digits, '.', '_' or '-', starting with a letter or digit."];
        }

        if (request.PolicyId is null)
        {
            errors["policyId"] = ["The policy to bind is required."];
        }

        if (request.Description is { Length: > _maxDescriptionLength })
        {
            errors["description"] = [$"The description can have at most {_maxDescriptionLength} characters."];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The alias is invalid.", errors);
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var policy = await db.Policies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PolicyId, cancellationToken);
        if (policy is null)
        {
            return Error.Validation("The alias is invalid.", new Dictionary<string, string[]> { ["policyId"] = ["No routing policy has this id."] });
        }

        var now = time.GetUtcNow();
        var existing = await db.Aliases.FirstOrDefaultAsync(a => a.Alias == alias, cancellationToken);
        var created = existing is null;
        Guid? previousPolicy = existing?.PolicyId;
        if (existing is null)
        {
            existing = new ModelAlias { Alias = alias, PolicyId = policy.Id, Description = request.Description, UpdatedAt = now };
            db.Aliases.Add(existing);
        }
        else
        {
            existing.PolicyId = policy.Id;
            existing.Description = request.Description;
            existing.UpdatedAt = now;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (ConfigWrite.IsUniqueViolation(exception))
        {
            return new Error(ErrorCode.Conflict, "The alias was created concurrently. Retry.");
        }

        var entry = new AuditEntry(RoutingAuditActions.AliasBound, "model_alias", alias)
        {
            Before = previousPolicy is { } before ? new Dictionary<string, object?> { ["policyId"] = before.ToString("D", CultureInfo.InvariantCulture) } : null,
            After = new Dictionary<string, object?> { ["policyId"] = policy.Id.ToString("D", CultureInfo.InvariantCulture), ["policyName"] = policy.Name },
        };
        await ConfigWrite.StageAsync(transaction.GetDbTransaction(), audit, [entry], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AliasBinding(new AliasView(alias, policy.Id, policy.Name, existing.Description, now), created);
    }

    public async Task<Result> UnbindAsync(string alias, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var existing = await db.Aliases.FirstOrDefaultAsync(a => a.Alias == alias, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        db.Aliases.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
        var entry = new AuditEntry(RoutingAuditActions.AliasUnbound, "model_alias", alias)
        {
            Before = new Dictionary<string, object?> { ["policyId"] = existing.PolicyId.ToString("D", CultureInfo.InvariantCulture) },
        };
        await ConfigWrite.StageAsync(transaction.GetDbTransaction(), audit, [entry], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }

    internal static bool IsValidAlias(string? alias) => alias is not null && AliasPattern().IsMatch(alias);

    private static Error NotFound() => new(ErrorCode.NotFound, "The alias was not found.");

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex AliasPattern();
}
