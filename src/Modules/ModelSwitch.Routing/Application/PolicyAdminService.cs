using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Routing.Contracts;
using ModelSwitch.Routing.Domain;
using ModelSwitch.Routing.Infrastructure;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Pagination;

namespace ModelSwitch.Routing.Application;

internal sealed record CreatePolicyRequest(string? Name, JsonElement? Definition);

/// <summary><c>expectedVersion</c> is the version the client edited from; a newer one saved meanwhile is a <c>409 conflict</c>.</summary>
internal sealed record UpdatePolicyRequest(JsonElement? Definition, int? ExpectedVersion);

internal sealed record ValidatePolicyRequest(JsonElement? Definition);

internal sealed record PolicyValidationView(bool Valid, IReadOnlyDictionary<string, Guid> ResolvedCandidates);

/// <summary>The latest Policy Version of a policy and the Model Aliases that currently point at it.</summary>
internal sealed record PolicyView(
    Guid Id,
    string Name,
    int Version,
    JsonNode? Definition,
    IReadOnlyDictionary<string, Guid> ResolvedCandidates,
    IReadOnlyList<string> Aliases,
    DateTimeOffset CreatedAt,
    DateTimeOffset VersionCreatedAt);

internal sealed record PolicyVersionView(
    Guid PolicyId,
    int Version,
    JsonNode? Definition,
    IReadOnlyDictionary<string, Guid> ResolvedCandidates,
    DateTimeOffset CreatedAt,
    Guid? CreatedByAdminId);

/// <summary>
/// Routing Policy management (FR-04, FR-11; Owner writes). A Policy Version is validated against the Registry as it is
/// now, then stored with its <c>resolved_candidates</c> fixed; versions are only ever inserted. The policy's
/// <c>latest_version</c> pointer, the version row, the Audit Record and the config version bump share one transaction.
/// </summary>
internal sealed partial class PolicyAdminService(
    IDbContextFactory<RoutingDbContext> contexts,
    IPolicyDefinitionValidator validator,
    ICatalogSnapshotSource catalog,
    IAuditWriter audit,
    ICurrentAdministrator current,
    IGuidGenerator ids,
    TimeProvider time)
{
    public async Task<Result<PagedResult<PolicyView>>> ListAsync(string? limit, string? cursor, CancellationToken cancellationToken)
    {
        var page = Pagination.Parse(limit, cursor);
        if (page.IsFailure)
        {
            return page.Error;
        }

        if (!TryParseGuidCursor(page.Value.After, out var after))
        {
            return InvalidCursor();
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var policies = db.Policies.AsNoTracking();
        if (after is { } last)
        {
            policies = policies.Where(p => p.Id.CompareTo(last) > 0);
        }

        var rows = await (
            from p in policies.OrderBy(p => p.Id).Take(page.Value.Limit + 1)
            join v in db.PolicyVersions.AsNoTracking() on new { PolicyId = p.Id, Version = p.LatestVersion } equals new { v.PolicyId, v.Version }
            orderby p.Id
            select new { Policy = p, Version = v })
            .ToListAsync(cancellationToken);

        var ids = rows.Select(r => r.Policy.Id).ToList();
        var aliases = (await db.Aliases.AsNoTracking().Where(a => ids.Contains(a.PolicyId)).Select(a => new { a.PolicyId, a.Alias }).ToListAsync(cancellationToken))
            .GroupBy(a => a.PolicyId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(a => a.Alias).Order(StringComparer.Ordinal)]);

        var views = rows
            .Select(r => new PolicyView(
                r.Policy.Id,
                r.Policy.Name,
                r.Version.Version,
                JsonNode.Parse(r.Version.DefinitionJson),
                ParseResolved(r.Version.ResolvedCandidatesJson),
                aliases.TryGetValue(r.Policy.Id, out var names) ? names : [],
                r.Policy.CreatedAt,
                r.Version.CreatedAt))
            .ToList();
        return Pagination.Page(views, page.Value.Limit, v => v.Id.ToString("D", CultureInfo.InvariantCulture));
    }

    public async Task<Result<PagedResult<PolicyVersionView>>> ListVersionsAsync(Guid policyId, string? limit, string? cursor, CancellationToken cancellationToken)
    {
        var page = Pagination.Parse(limit, cursor);
        if (page.IsFailure)
        {
            return page.Error;
        }

        int? before = null;
        if (page.Value.After is not null)
        {
            if (!int.TryParse(page.Value.After, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return InvalidCursor();
            }

            before = parsed;
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (!await db.Policies.AsNoTracking().AnyAsync(p => p.Id == policyId, cancellationToken))
        {
            return PolicyNotFound();
        }

        var query = db.PolicyVersions.AsNoTracking().Where(v => v.PolicyId == policyId);
        if (before is { } last)
        {
            query = query.Where(v => v.Version < last);
        }

        var rows = await query.OrderByDescending(v => v.Version).Take(page.Value.Limit + 1).ToListAsync(cancellationToken);
        return Pagination.Page(rows.Select(ToVersionView).ToList(), page.Value.Limit, v => v.Version.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<Result<PolicyVersionView>> GetVersionAsync(Guid policyId, int version, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.PolicyVersions.AsNoTracking().FirstOrDefaultAsync(v => v.PolicyId == policyId && v.Version == version, cancellationToken);
        return row is null ? new Error(ErrorCode.NotFound, "The policy version was not found.") : ToVersionView(row);
    }

    /// <summary>Dry run: the same structural and semantic checks as a save, writing nothing.</summary>
    public async Task<Result<PolicyValidationView>> ValidateAsync(ValidatePolicyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var checkedDefinition = await CheckDefinitionAsync(request.Definition, cancellationToken);
        return checkedDefinition.IsFailure
            ? checkedDefinition.Error
            : new PolicyValidationView(true, checkedDefinition.Value.Resolved);
    }

    public async Task<Result<PolicyVersionView>> CreateAsync(CreatePolicyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || !NamePattern().IsMatch(name))
        {
            errors["name"] = ["The name must be 1 to 63 characters: lower-case letters, digits, '.', '_' or '-', starting with a letter or digit."];
        }

        var definition = await CheckDefinitionAsync(request.Definition, cancellationToken);
        if (definition.IsFailure)
        {
            foreach (var (path, messages) in definition.Error.FieldErrors ?? new Dictionary<string, string[]>())
            {
                errors[path] = messages;
            }
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The routing policy is invalid.", errors);
        }

        var now = time.GetUtcNow();
        var policy = new RoutingPolicy { Id = ids.NewGuid(), Name = name!, LatestVersion = 1, CreatedAt = now };
        var version = NewVersion(policy.Id, 1, definition.Value, now);

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Policies.Add(policy);
        db.PolicyVersions.Add(version);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return new Error(ErrorCode.Conflict, "A routing policy with this name already exists.");
        }

        var entry = new AuditEntry(RoutingAuditActions.PolicyCreated, "routing_policy", policy.Id.ToString("D", CultureInfo.InvariantCulture))
        {
            After = Summarise(policy.Name, 1, definition.Value.Strategy),
        };
        await ConfigWrite.CommitAsync(db, transaction, audit, [entry], cancellationToken);
        return ToVersionView(version);
    }

    public async Task<Result<PolicyVersionView>> CreateVersionAsync(Guid policyId, UpdatePolicyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        if (request.ExpectedVersion is null or < 1)
        {
            errors["expectedVersion"] = ["The version you are editing from is required."];
        }

        var definition = await CheckDefinitionAsync(request.Definition, cancellationToken);
        if (definition.IsFailure)
        {
            foreach (var (path, messages) in definition.Error.FieldErrors ?? new Dictionary<string, string[]>())
            {
                errors[path] = messages;
            }
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The routing policy is invalid.", errors);
        }

        var expected = request.ExpectedVersion!.Value;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var policy = await db.Policies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == policyId, cancellationToken);
        if (policy is null)
        {
            return PolicyNotFound();
        }

        var stale = new Error(ErrorCode.Conflict, $"The policy is at version {policy.LatestVersion}, not {expected}. Reload and retry.");
        if (policy.LatestVersion != expected)
        {
            return stale;
        }

        // The pointer moves only if nobody saved a version since it was read, so two concurrent saves cannot both win.
        var moved = await db.Policies.Where(p => p.Id == policyId && p.LatestVersion == expected)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.LatestVersion, expected + 1), cancellationToken);
        if (moved == 0)
        {
            return stale;
        }

        var version = NewVersion(policyId, expected + 1, definition.Value, time.GetUtcNow());
        db.PolicyVersions.Add(version);
        await db.SaveChangesAsync(cancellationToken);

        var entry = new AuditEntry(RoutingAuditActions.PolicyVersioned, "routing_policy", policyId.ToString("D", CultureInfo.InvariantCulture))
        {
            Before = Summarise(policy.Name, expected, null),
            After = Summarise(policy.Name, expected + 1, definition.Value.Strategy),
        };
        await ConfigWrite.CommitAsync(db, transaction, audit, [entry], cancellationToken);
        return ToVersionView(version);
    }

    private PolicyVersion NewVersion(Guid policyId, int version, CheckedDefinition definition, DateTimeOffset now) => new()
    {
        PolicyId = policyId,
        Version = version,
        DefinitionJson = definition.Json,
        ResolvedCandidatesJson = JsonSerializer.Serialize(definition.Resolved),
        CreatedAt = now,
        CreatedByAdminId = current.Current?.Id,
    };

    /// <summary>The structural and semantic checks against the Registry as it is now. Errors are keyed <c>definition</c> or <c>definition.&lt;path&gt;</c>.</summary>
    private async Task<Result<CheckedDefinition>> CheckDefinitionAsync(JsonElement? definition, CancellationToken cancellationToken)
    {
        if (definition is not { ValueKind: JsonValueKind.Object } root)
        {
            return Error.Validation("The routing policy is invalid.", new Dictionary<string, string[]> { ["definition"] = ["The definition must be a JSON object."] });
        }

        var entries = (await catalog.LoadAsync(cancellationToken)).Entries;
        var models = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            models[$"{entry.ProviderName}/{entry.ProviderModelIdentifier}"] = entry.ProviderModelId;
        }

        var providers = entries.Select(e => e.ProviderName).ToHashSet(StringComparer.Ordinal);
        var json = root.GetRawText();
        var result = validator.Validate(json, new PolicyReferenceData(models, providers));
        if (!result.IsValid)
        {
            var errors = result.Errors
                .GroupBy(e => e.Path.Length == 0 ? "definition" : "definition." + e.Path, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray(), StringComparer.Ordinal);
            return Error.Validation("The routing policy definition is invalid.", errors);
        }

        return new CheckedDefinition(json, result.ResolvedCandidates, result.Definition!.Strategy.ToString().ToLowerInvariant());
    }

    private static Dictionary<string, object?> Summarise(string name, int version, string? strategy) => new()
    {
        ["name"] = name,
        ["version"] = version,
        ["strategy"] = strategy,
    };

    private static PolicyVersionView ToVersionView(PolicyVersion version) => new(
        version.PolicyId,
        version.Version,
        JsonNode.Parse(version.DefinitionJson),
        ParseResolved(version.ResolvedCandidatesJson),
        version.CreatedAt,
        version.CreatedByAdminId);

    private static Dictionary<string, Guid> ParseResolved(string json) => JsonSerializer.Deserialize<Dictionary<string, Guid>>(json) ?? [];

    private static bool TryParseGuidCursor(string? text, out Guid? value)
    {
        value = null;
        if (text is null)
        {
            return true;
        }

        if (!Guid.TryParse(text, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool IsUniqueViolation(DbUpdateException exception) => ConfigWrite.IsUniqueViolation(exception);

    private static Error InvalidCursor()
        => Error.Validation("The pagination query is invalid.", new Dictionary<string, string[]> { ["cursor"] = ["The cursor is not valid."] });

    private static Error PolicyNotFound() => new(ErrorCode.NotFound, "The routing policy was not found.");

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,62}$")]
    private static partial Regex NamePattern();

    private sealed record CheckedDefinition(string Json, IReadOnlyDictionary<string, Guid> Resolved, string Strategy);
}
