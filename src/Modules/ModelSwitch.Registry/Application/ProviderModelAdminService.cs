using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Registry.Domain;
using ModelSwitch.Registry.Infrastructure;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Pagination;

namespace ModelSwitch.Registry.Application;

internal sealed record CreateProviderModelRequest(string? ProviderModelId, string? DisplayName, JsonElement? Capabilities, bool? Enabled);

/// <summary>A null field is left unchanged. The Provider's model id is fixed at creation: Policy Versions resolve to the row.</summary>
internal sealed record UpdateProviderModelRequest(string? DisplayName, JsonElement? Capabilities, bool? Enabled);

internal sealed record ProviderModelView(
    Guid Id,
    Guid ProviderId,
    string ProviderModelId,
    string DisplayName,
    JsonNode? Capabilities,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Provider Model management (Owner writes): Capabilities and the enabled flag. Same transaction rules as <see cref="ProviderAdminService"/>.</summary>
internal sealed class ProviderModelAdminService(
    IDbContextFactory<RegistryDbContext> contexts,
    IAuditWriter audit,
    IGuidGenerator ids,
    TimeProvider time)
{
    public async Task<Result<PagedResult<ProviderModelView>>> ListAsync(Guid providerId, string? limit, string? cursor, CancellationToken cancellationToken)
    {
        var page = Pagination.Parse(limit, cursor);
        if (page.IsFailure)
        {
            return page.Error;
        }

        Guid? after = null;
        if (page.Value.After is not null)
        {
            if (!Guid.TryParse(page.Value.After, out var parsed))
            {
                return Error.Validation("The pagination query is invalid.", new Dictionary<string, string[]> { ["cursor"] = ["The cursor is not valid."] });
            }

            after = parsed;
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (!await db.Providers.AsNoTracking().AnyAsync(p => p.Id == providerId, cancellationToken))
        {
            return ProviderNotFound();
        }

        var query = db.ProviderModels.AsNoTracking().Where(m => m.ProviderId == providerId);
        if (after is { } last)
        {
            query = query.Where(m => m.Id.CompareTo(last) > 0);
        }

        var rows = await query.OrderBy(m => m.Id).Take(page.Value.Limit + 1).ToListAsync(cancellationToken);
        return Pagination.Page(rows.Select(ToView).ToList(), page.Value.Limit, v => v.Id.ToString("D", CultureInfo.InvariantCulture));
    }

    public async Task<Result<ProviderModelView>> CreateAsync(Guid providerId, CreateProviderModelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        var identifier = AdminRules.ProviderModelIdentifier(request.ProviderModelId, errors);
        var displayName = request.DisplayName is null ? identifier : AdminRules.DisplayName(request.DisplayName, errors);
        var capabilities = AdminRules.Capabilities(request.Capabilities, errors);
        if (errors.Count > 0)
        {
            return Error.Validation("The provider model is invalid.", errors);
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var provider = await db.Providers.AsNoTracking().FirstOrDefaultAsync(p => p.Id == providerId, cancellationToken);
        if (provider is null)
        {
            return ProviderNotFound();
        }

        var now = time.GetUtcNow();
        var model = new ProviderModel
        {
            Id = ids.NewGuid(),
            ProviderId = providerId,
            ProviderModelIdentifier = identifier!,
            DisplayName = displayName!,
            CapabilitiesJson = capabilities!,
            Enabled = request.Enabled ?? true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ProviderModels.Add(model);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (ConfigWrite.IsUniqueViolation(exception))
        {
            return new Error(ErrorCode.Conflict, "This provider already has a model with this id.");
        }

        var entry = new AuditEntry(RegistryAuditActions.ProviderModelCreated, "provider_model", model.Id.ToString("D", CultureInfo.InvariantCulture))
        {
            After = Summarise(model, provider.Name),
        };
        await ConfigWrite.CommitAsync(db, transaction, audit, [entry], cancellationToken);
        return ToView(model);
    }

    public async Task<Result<ProviderModelView>> UpdateAsync(Guid modelId, UpdateProviderModelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        if (request is { DisplayName: null, Capabilities: null, Enabled: null })
        {
            errors["body"] = ["Give at least one of: displayName, capabilities, enabled."];
        }

        var displayName = request.DisplayName is null ? null : AdminRules.DisplayName(request.DisplayName, errors);
        var capabilities = request.Capabilities is null ? null : AdminRules.Capabilities(request.Capabilities, errors);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var model = await db.ProviderModels.FirstOrDefaultAsync(m => m.Id == modelId, cancellationToken);
        if (model is null)
        {
            return new Error(ErrorCode.NotFound, "The provider model was not found.");
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The provider model is invalid.", errors);
        }

        var providerName = await db.Providers.AsNoTracking().Where(p => p.Id == model.ProviderId).Select(p => p.Name).FirstAsync(cancellationToken);
        var before = Summarise(model, providerName);
        model.DisplayName = displayName ?? model.DisplayName;
        model.CapabilitiesJson = capabilities ?? model.CapabilitiesJson;
        model.Enabled = request.Enabled ?? model.Enabled;
        var after = Summarise(model, providerName);
        if (before.SequenceEqual(after))
        {
            return ToView(model);
        }

        model.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        var entry = new AuditEntry(RegistryAuditActions.ProviderModelModified, "provider_model", modelId.ToString("D", CultureInfo.InvariantCulture))
        {
            Before = before,
            After = after,
        };
        await ConfigWrite.CommitAsync(db, transaction, audit, [entry], cancellationToken);
        return ToView(model);
    }

    private static Dictionary<string, object?> Summarise(ProviderModel model, string providerName) => new()
    {
        ["provider"] = providerName,
        ["providerModelId"] = model.ProviderModelIdentifier,
        ["displayName"] = model.DisplayName,
        ["enabled"] = model.Enabled,
        ["capabilities"] = JsonSerializer.Serialize(JsonNode.Parse(model.CapabilitiesJson)),
    };

    private static ProviderModelView ToView(ProviderModel model) => new(
        model.Id,
        model.ProviderId,
        model.ProviderModelIdentifier,
        model.DisplayName,
        JsonNode.Parse(model.CapabilitiesJson),
        model.Enabled,
        model.CreatedAt,
        model.UpdatedAt);

    private static Error ProviderNotFound() => new(ErrorCode.NotFound, "The provider was not found.");
}
