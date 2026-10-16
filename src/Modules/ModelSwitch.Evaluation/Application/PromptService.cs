using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ModelSwitch.Evaluation.Domain;
using ModelSwitch.Evaluation.Domain.Prompts;
using ModelSwitch.Evaluation.Infrastructure;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Pagination;

namespace ModelSwitch.Evaluation.Application;

internal sealed record CreatePromptRequest(Guid? TenantId, string? Name);

internal sealed record PromptMessageRequest(string? Role, string? Content);

/// <summary>A Prompt Version's template: messages with <c>{{var}}</c> placeholders and an optional OpenAI-style response format.</summary>
internal sealed record CreatePromptVersionRequest(IReadOnlyList<PromptMessageRequest?>? Messages, JsonElement? ResponseFormat);

internal sealed record PromptView(Guid Id, Guid TenantId, string Name, int LatestVersion, DateTimeOffset CreatedAt);

internal sealed record PromptMessageView(string Role, string Content);

internal sealed record PromptTemplateView(IReadOnlyList<PromptMessageView> Messages, JsonNode? ResponseFormat);

internal sealed record PromptVersionView(Guid PromptId, int Version, PromptTemplateView Template, DateTimeOffset CreatedAt, Guid? CreatedByAdminId);

/// <summary>
/// Prompts and their immutable Prompt Versions (evaluation.md section 2, FR-11). Viewers read and Operators write inside their
/// Tenant Grant; a Prompt of a Tenant outside it is 404. A new version takes the next number atomically, with its Audit Record in
/// the same transaction. Template content is never logged and never copied into an Audit Record.
/// </summary>
internal sealed class PromptService(
    IDbContextFactory<EvaluationDbContext> contexts,
    EvaluationAccess access,
    ITenantDirectory tenants,
    IAuditWriter audit,
    IGuidGenerator ids,
    TimeProvider time)
{
    private static readonly JsonSerializerOptions _templateJson = new(JsonSerializerDefaults.Web);

    public async Task<Result<PagedResult<PromptView>>> ListAsync(string? tenantId, string? limit, string? cursor, CancellationToken cancellationToken)
    {
        var query = access.ListQuery(tenantId, limit, cursor);
        if (query.IsFailure)
        {
            return query.Error;
        }

        var (filter, page, before) = query.Value;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var prompts = db.Prompts.AsNoTracking();
        if (filter is { } granted)
        {
            prompts = prompts.Where(p => granted.Contains(p.TenantId));
        }

        if (before is { } last)
        {
            prompts = prompts.Where(p => p.Id.CompareTo(last) < 0);
        }

        var rows = await prompts.OrderByDescending(p => p.Id).Take(page.Limit + 1).ToListAsync(cancellationToken);
        return Pagination.Page(rows.Select(ToView).ToList(), page.Limit, v => v.Id.ToString("D", CultureInfo.InvariantCulture));
    }

    public async Task<Result<PromptView>> CreateAsync(CreatePromptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        if (request.TenantId is null || request.TenantId == Guid.Empty)
        {
            errors["tenantId"] = ["The tenantId is required."];
        }

        var name = ResourceNames.Validate(request.Name, out var nameError);
        if (nameError is not null)
        {
            errors["name"] = [nameError];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The prompt is invalid.", errors);
        }

        var tenantId = request.TenantId!.Value;
        var granted = access.CheckTenant(tenantId);
        if (granted.IsFailure)
        {
            return granted.Error;
        }

        if (!await tenants.ExistsAsync(tenantId, cancellationToken))
        {
            return new Error(ErrorCode.NotFound, "The resource was not found.");
        }

        var prompt = new Prompt { Id = ids.NewGuid(), TenantId = tenantId, Name = name!, LatestVersion = 0, CreatedAt = time.GetUtcNow() };
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Prompts.Add(prompt);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (ConfigWrite.IsUniqueViolation(exception))
        {
            return new Error(ErrorCode.Conflict, "The tenant already has a prompt with this name.");
        }

        var entry = new AuditEntry(EvaluationAuditActions.PromptCreated, "prompt", prompt.Id.ToString("D", CultureInfo.InvariantCulture))
        {
            TenantId = tenantId,
            After = new Dictionary<string, object?> { ["name"] = prompt.Name },
        };
        await audit.WriteAsync(entry, transaction.GetDbTransaction(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToView(prompt);
    }

    public async Task<Result<PromptView>> GetAsync(Guid promptId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var prompt = await FindAsync(db, promptId, cancellationToken);
        return prompt.IsFailure ? prompt.Error : ToView(prompt.Value);
    }

    public async Task<Result<PagedResult<PromptVersionView>>> ListVersionsAsync(Guid promptId, string? limit, string? cursor, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var prompt = await FindAsync(db, promptId, cancellationToken);
        if (prompt.IsFailure)
        {
            return prompt.Error;
        }

        var page = Pagination.Parse(limit, cursor);
        if (page.IsFailure)
        {
            return page.Error;
        }

        int? before = null;
        if (page.Value.After is { } after)
        {
            if (!int.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return EvaluationAccess.InvalidCursor();
            }

            before = parsed;
        }

        var query = db.PromptVersions.AsNoTracking().Where(v => v.PromptId == promptId);
        if (before is { } last)
        {
            query = query.Where(v => v.Version < last);
        }

        var rows = await query.OrderByDescending(v => v.Version).Take(page.Value.Limit + 1).ToListAsync(cancellationToken);
        return Pagination.Page(rows.Select(ToView).ToList(), page.Value.Limit, v => v.Version.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<Result<PromptVersionView>> GetVersionAsync(Guid promptId, int version, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var prompt = await FindAsync(db, promptId, cancellationToken);
        if (prompt.IsFailure)
        {
            return prompt.Error;
        }

        var row = await db.PromptVersions.AsNoTracking().FirstOrDefaultAsync(v => v.PromptId == promptId && v.Version == version, cancellationToken);
        return row is null ? new Error(ErrorCode.NotFound, "The prompt version was not found.") : ToView(row);
    }

    /// <summary>Validates the template, then appends it as the next Prompt Version. Earlier versions never change.</summary>
    public async Task<Result<PromptVersionView>> CreateVersionAsync(Guid promptId, CreatePromptVersionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var prompt = await FindAsync(db, promptId, cancellationToken);
        if (prompt.IsFailure)
        {
            return prompt.Error;
        }

        var template = PromptTemplateRules.Validate(
            request.Messages?.Select(m => m is null ? null : new PromptMessageInput(m.Role, m.Content)).ToList(),
            request.ResponseFormat);
        if (template.IsFailure)
        {
            return template.Error;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var version = await db.Database
            .SqlQuery<int>($"UPDATE evaluation.prompt SET latest_version = latest_version + 1 WHERE id = {promptId} RETURNING latest_version AS \"Value\"")
            .ToListAsync(cancellationToken);
        if (version.Count == 0)
        {
            return PromptNotFound();
        }

        var row = new PromptVersion
        {
            PromptId = promptId,
            Version = version[0],
            TemplateJson = template.Value,
            CreatedAt = time.GetUtcNow(),
            CreatedByAdminId = access.AdministratorId,
        };
        db.PromptVersions.Add(row);
        await db.SaveChangesAsync(cancellationToken);

        var entry = new AuditEntry(EvaluationAuditActions.PromptVersioned, "prompt", promptId.ToString("D", CultureInfo.InvariantCulture))
        {
            TenantId = prompt.Value.TenantId,
            After = new Dictionary<string, object?>
            {
                ["name"] = prompt.Value.Name,
                ["version"] = row.Version,
                ["messages"] = request.Messages!.Count,
            },
        };
        await audit.WriteAsync(entry, transaction.GetDbTransaction(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToView(row);
    }

    private async Task<Result<Prompt>> FindAsync(EvaluationDbContext db, Guid promptId, CancellationToken cancellationToken)
    {
        var prompt = await db.Prompts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == promptId, cancellationToken);
        if (prompt is null || access.CheckTenant(prompt.TenantId).IsFailure)
        {
            return PromptNotFound();
        }

        return prompt;
    }

    private static Error PromptNotFound() => new(ErrorCode.NotFound, "The prompt was not found.");

    private static PromptView ToView(Prompt prompt) => new(prompt.Id, prompt.TenantId, prompt.Name, prompt.LatestVersion, prompt.CreatedAt);

    private static PromptVersionView ToView(PromptVersion version)
    {
        var stored = JsonSerializer.Deserialize<StoredTemplate>(version.TemplateJson, _templateJson)!;
        return new PromptVersionView(
            version.PromptId,
            version.Version,
            new PromptTemplateView(stored.Messages, stored.ResponseFormat),
            version.CreatedAt,
            version.CreatedByAdminId);
    }

    private sealed record StoredTemplate(IReadOnlyList<PromptMessageView> Messages, JsonNode? ResponseFormat);
}
