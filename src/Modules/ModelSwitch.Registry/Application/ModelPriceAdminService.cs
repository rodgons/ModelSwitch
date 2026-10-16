using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Registry.Domain;
using ModelSwitch.Registry.Infrastructure;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Pagination;

namespace ModelSwitch.Registry.Application;

/// <summary><c>effectiveFrom</c> defaults to now and may not be in the past. <c>cachedInputPerMillion</c> null means no discount.</summary>
internal sealed record CreateModelPriceRequest(decimal? InputPerMillion, decimal? CachedInputPerMillion, decimal? OutputPerMillion, string? Currency, DateTimeOffset? EffectiveFrom);

internal sealed record ModelPriceView(
    Guid Id,
    Guid ProviderModelId,
    decimal InputPerMillion,
    decimal? CachedInputPerMillion,
    decimal OutputPerMillion,
    string Currency,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset CreatedAt,
    Guid? CreatedByAdminId,
    bool InForce);

/// <summary>
/// Price history (FR-06; Owner writes). Prices are append-only: a change is a new <c>model_price</c> row with a later
/// <c>effective_from</c>, never an update, so every Usage Record keeps the Price it was costed with.
/// </summary>
internal sealed class ModelPriceAdminService(
    IDbContextFactory<RegistryDbContext> contexts,
    IAuditWriter audit,
    ICurrentAdministrator current,
    IGuidGenerator ids,
    TimeProvider time)
{
    public async Task<Result<PagedResult<ModelPriceView>>> ListAsync(Guid modelId, string? limit, string? cursor, CancellationToken cancellationToken)
    {
        var page = Pagination.Parse(limit, cursor);
        if (page.IsFailure)
        {
            return page.Error;
        }

        long? afterTicks = null;
        if (page.Value.After is not null)
        {
            if (!long.TryParse(page.Value.After, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return Error.Validation("The pagination query is invalid.", new Dictionary<string, string[]> { ["cursor"] = ["The cursor is not valid."] });
            }

            afterTicks = ticks;
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (!await db.ProviderModels.AsNoTracking().AnyAsync(m => m.Id == modelId, cancellationToken))
        {
            return ModelNotFound();
        }

        var now = time.GetUtcNow();
        var inForce = await db.ModelPrices.AsNoTracking()
            .Where(p => p.ProviderModelId == modelId && p.EffectiveFrom <= now)
            .OrderByDescending(p => p.EffectiveFrom)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var query = db.ModelPrices.AsNoTracking().Where(p => p.ProviderModelId == modelId);
        if (afterTicks is { } last)
        {
            var lastTime = new DateTimeOffset(last, TimeSpan.Zero);
            query = query.Where(p => p.EffectiveFrom < lastTime);
        }

        var rows = await query.OrderByDescending(p => p.EffectiveFrom).Take(page.Value.Limit + 1).ToListAsync(cancellationToken);
        var views = rows.Select(p => ToView(p, p.Id == inForce)).ToList();
        return Pagination.Page(views, page.Value.Limit, v => v.EffectiveFrom.UtcTicks.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<Result<ModelPriceView>> CreateAsync(Guid modelId, CreateModelPriceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = time.GetUtcNow();
        var errors = new Dictionary<string, string[]>();
        var input = AdminRules.PricePerMillion(request.InputPerMillion, "inputPerMillion", required: true, errors);
        var cached = AdminRules.PricePerMillion(request.CachedInputPerMillion, "cachedInputPerMillion", required: false, errors);
        var output = AdminRules.PricePerMillion(request.OutputPerMillion, "outputPerMillion", required: true, errors);
        if (request.Currency is not null && request.Currency != "USD")
        {
            errors["currency"] = ["Only USD is accepted."];
        }

        var effectiveFrom = request.EffectiveFrom ?? now;
        if (effectiveFrom < now)
        {
            errors["effectiveFrom"] = ["A Price cannot take effect in the past: Usage Records already recorded keep the Price they were costed with."];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The price is invalid.", errors);
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var model = await db.ProviderModels.AsNoTracking().FirstOrDefaultAsync(m => m.Id == modelId, cancellationToken);
        if (model is null)
        {
            return ModelNotFound();
        }

        var price = new ModelPrice
        {
            Id = ids.NewGuid(),
            ProviderModelId = modelId,
            InputPerMillion = input!.Value,
            CachedInputPerMillion = cached,
            OutputPerMillion = output!.Value,
            Currency = "USD",
            EffectiveFrom = effectiveFrom,
            CreatedAt = now,
            CreatedByAdminId = current.Current?.Id,
        };
        db.ModelPrices.Add(price);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (ConfigWrite.IsUniqueViolation(exception))
        {
            return new Error(ErrorCode.Conflict, "This model already has a Price taking effect at exactly that time.");
        }

        var entry = new AuditEntry(RegistryAuditActions.ModelPriceCreated, "model_price", price.Id.ToString("D", CultureInfo.InvariantCulture))
        {
            After = new Dictionary<string, object?>
            {
                ["providerModelId"] = modelId.ToString("D", CultureInfo.InvariantCulture),
                ["inputPerMillion"] = price.InputPerMillion,
                ["cachedInputPerMillion"] = price.CachedInputPerMillion,
                ["outputPerMillion"] = price.OutputPerMillion,
                ["effectiveFrom"] = price.EffectiveFrom.ToString("O", CultureInfo.InvariantCulture),
            },
        };
        await ConfigWrite.CommitAsync(db, transaction, audit, [entry], cancellationToken);
        return ToView(price, inForce: price.EffectiveFrom <= now);
    }

    private static ModelPriceView ToView(ModelPrice price, bool inForce) => new(
        price.Id,
        price.ProviderModelId,
        price.InputPerMillion,
        price.CachedInputPerMillion,
        price.OutputPerMillion,
        price.Currency,
        price.EffectiveFrom,
        price.CreatedAt,
        price.CreatedByAdminId,
        inForce);

    private static Error ModelNotFound() => new(ErrorCode.NotFound, "The provider model was not found.");
}
