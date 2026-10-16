using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ModelSwitch.Evaluation.Domain;
using ModelSwitch.Evaluation.Domain.Datasets;
using ModelSwitch.Evaluation.Infrastructure;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Pagination;

namespace ModelSwitch.Evaluation.Application;

internal sealed record CreateDatasetRequest(Guid? TenantId, string? Name, string? TaskKind);

internal sealed record DatasetView(Guid Id, Guid TenantId, string Name, string TaskKind, int LatestVersion, DateTimeOffset CreatedAt);

/// <summary><c>scoringSchema</c>: <c>{taskKind, labels?}</c> for classification, <c>{taskKind, fields?}</c> for extraction.</summary>
internal sealed record DatasetVersionView(
    Guid DatasetId,
    int Version,
    int CaseCount,
    string ContentSha256,
    JsonNode? ScoringSchema,
    DateTimeOffset CreatedAt,
    Guid? CreatedByAdminId);

/// <summary>One Case. <c>id</c> is the gateway's; <c>externalId</c> is the JSONL <c>id</c>; <c>ordinal</c> is its place in the upload, from 0.</summary>
internal sealed record CaseView(Guid Id, string ExternalId, int Ordinal, JsonNode? Input, JsonNode? Expected, JsonNode? Metadata);

/// <summary>The raw upload of a Dataset Version, as the transport received it.</summary>
internal sealed record DatasetUpload(string? ContentType, long? ContentLength, Stream Body);

/// <summary>
/// Datasets and their immutable Dataset Versions (evaluation.md section 1, FR-09). Viewers read and Operators write inside their
/// Tenant Grant; a Dataset of a Tenant outside it is 404. An upload is validated as a whole, then the new version number, the
/// version row, its Cases and the Audit Record are written in one transaction. Case content is never logged.
/// </summary>
internal sealed class DatasetService(
    IDbContextFactory<EvaluationDbContext> contexts,
    EvaluationAccess access,
    ITenantDirectory tenants,
    IAuditWriter audit,
    IGuidGenerator ids,
    TimeProvider time)
{
    /// <summary>The media types an upload may declare. Parameters such as <c>charset=utf-8</c> are allowed; the body is always UTF-8.</summary>
    public static readonly IReadOnlyList<string> UploadMediaTypes = ["application/x-ndjson", "application/jsonl", "application/x-jsonlines"];

    public async Task<Result<PagedResult<DatasetView>>> ListAsync(string? tenantId, string? limit, string? cursor, CancellationToken cancellationToken)
    {
        var query = access.ListQuery(tenantId, limit, cursor);
        if (query.IsFailure)
        {
            return query.Error;
        }

        var (filter, page, before) = query.Value;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var datasets = db.Datasets.AsNoTracking();
        if (filter is { } granted)
        {
            datasets = datasets.Where(d => granted.Contains(d.TenantId));
        }

        if (before is { } last)
        {
            datasets = datasets.Where(d => d.Id.CompareTo(last) < 0);
        }

        var rows = await datasets.OrderByDescending(d => d.Id).Take(page.Limit + 1).ToListAsync(cancellationToken);
        return Pagination.Page(rows.Select(ToView).ToList(), page.Limit, v => v.Id.ToString("D", CultureInfo.InvariantCulture));
    }

    public async Task<Result<DatasetView>> CreateAsync(CreateDatasetRequest request, CancellationToken cancellationToken)
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

        if (!TaskKinds.TryParse(request.TaskKind, out _))
        {
            errors["taskKind"] = ["The taskKind must be extraction, classification or free_text."];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The dataset is invalid.", errors);
        }

        var tenantId = request.TenantId!.Value;
        var tenant = await RequireTenantAsync(tenantId, cancellationToken);
        if (tenant.IsFailure)
        {
            return tenant.Error;
        }

        var dataset = new Dataset
        {
            Id = ids.NewGuid(),
            TenantId = tenantId,
            Name = name!,
            TaskKind = request.TaskKind!,
            LatestVersion = 0,
            CreatedAt = time.GetUtcNow(),
        };

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Datasets.Add(dataset);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (ConfigWrite.IsUniqueViolation(exception))
        {
            return new Error(ErrorCode.Conflict, "The tenant already has a dataset with this name.");
        }

        var entry = new AuditEntry(EvaluationAuditActions.DatasetCreated, "dataset", dataset.Id.ToString("D", CultureInfo.InvariantCulture))
        {
            TenantId = tenantId,
            After = new Dictionary<string, object?> { ["name"] = dataset.Name, ["taskKind"] = dataset.TaskKind },
        };
        await audit.WriteAsync(entry, transaction.GetDbTransaction(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToView(dataset);
    }

    public async Task<Result<DatasetView>> GetAsync(Guid datasetId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var dataset = await FindAsync(db, datasetId, cancellationToken);
        return dataset.IsFailure ? dataset.Error : ToView(dataset.Value);
    }

    public async Task<Result<PagedResult<DatasetVersionView>>> ListVersionsAsync(Guid datasetId, string? limit, string? cursor, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var dataset = await FindAsync(db, datasetId, cancellationToken);
        if (dataset.IsFailure)
        {
            return dataset.Error;
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

        var query = db.DatasetVersions.AsNoTracking().Where(v => v.DatasetId == datasetId);
        if (before is { } last)
        {
            query = query.Where(v => v.Version < last);
        }

        var rows = await query.OrderByDescending(v => v.Version).Take(page.Value.Limit + 1).ToListAsync(cancellationToken);
        return Pagination.Page(rows.Select(ToView).ToList(), page.Value.Limit, v => v.Version.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<Result<DatasetVersionView>> GetVersionAsync(Guid datasetId, int version, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var dataset = await FindAsync(db, datasetId, cancellationToken);
        if (dataset.IsFailure)
        {
            return dataset.Error;
        }

        var row = await db.DatasetVersions.AsNoTracking().FirstOrDefaultAsync(v => v.DatasetId == datasetId && v.Version == version, cancellationToken);
        return row is null ? VersionNotFound() : ToView(row);
    }

    /// <summary>
    /// Creates the next Dataset Version from a JSONL upload: 404 for a Dataset outside the grant, 415 for another media type, 413
    /// above <see cref="DatasetJsonl.MaxBytes"/>, 400 <c>validation_failed</c> for any invalid line. Concurrent uploads each get
    /// their own number, because the increment of <c>latest_version</c> locks the Dataset row until the version is committed.
    /// </summary>
    public async Task<Result<DatasetVersionView>> UploadVersionAsync(Guid datasetId, DatasetUpload upload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var dataset = await FindAsync(db, datasetId, cancellationToken);
        if (dataset.IsFailure)
        {
            return dataset.Error;
        }

        if (!IsJsonl(upload.ContentType))
        {
            return new Error(ErrorCode.UnsupportedMediaType, $"Upload the Cases as JSONL with Content-Type {UploadMediaTypes[0]}.");
        }

        if (upload.ContentLength > DatasetJsonl.MaxBytes)
        {
            return TooLarge();
        }

        var bytes = await ReadBoundedAsync(upload.Body, DatasetJsonl.MaxBytes, cancellationToken);
        if (bytes is null)
        {
            return TooLarge();
        }

        if (!TaskKinds.TryParse(dataset.Value.TaskKind, out var kind))
        {
            throw new InvalidOperationException("The dataset has an unknown task kind.");
        }

        var parsed = DatasetJsonl.Parse(bytes, kind);
        if (parsed.IsFailure)
        {
            return parsed.Error;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var version = await db.Database
            .SqlQuery<int>($"UPDATE evaluation.dataset SET latest_version = latest_version + 1 WHERE id = {datasetId} RETURNING latest_version AS \"Value\"")
            .ToListAsync(cancellationToken);
        if (version.Count == 0)
        {
            return DatasetNotFound();
        }

        var row = new DatasetVersion
        {
            DatasetId = datasetId,
            Version = version[0],
            CaseCount = parsed.Value.Cases.Count,
            ContentSha256 = parsed.Value.ContentSha256,
            ScoringSchemaJson = parsed.Value.ScoringSchemaJson,
            CreatedAt = time.GetUtcNow(),
            CreatedByAdminId = access.AdministratorId,
        };
        db.DatasetVersions.Add(row);
        db.Cases.AddRange(parsed.Value.Cases.Select(c => new Case
        {
            Id = ids.NewGuid(),
            DatasetId = datasetId,
            DatasetVersion = row.Version,
            ExternalId = c.ExternalId,
            InputJson = c.InputJson,
            ExpectedJson = c.ExpectedJson,
            MetadataJson = c.MetadataJson,
            Ordinal = c.Ordinal,
        }));
        await db.SaveChangesAsync(cancellationToken);

        var entry = new AuditEntry(EvaluationAuditActions.DatasetVersioned, "dataset", datasetId.ToString("D", CultureInfo.InvariantCulture))
        {
            TenantId = dataset.Value.TenantId,
            After = new Dictionary<string, object?>
            {
                ["name"] = dataset.Value.Name,
                ["version"] = row.Version,
                ["caseCount"] = row.CaseCount,
                ["contentSha256"] = row.ContentSha256,
            },
        };
        await audit.WriteAsync(entry, transaction.GetDbTransaction(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToView(row);
    }

    public async Task<Result<PagedResult<CaseView>>> ListCasesAsync(Guid datasetId, int version, string? limit, string? cursor, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var dataset = await FindAsync(db, datasetId, cancellationToken);
        if (dataset.IsFailure)
        {
            return dataset.Error;
        }

        if (!await db.DatasetVersions.AsNoTracking().AnyAsync(v => v.DatasetId == datasetId && v.Version == version, cancellationToken))
        {
            return VersionNotFound();
        }

        var page = Pagination.Parse(limit, cursor);
        if (page.IsFailure)
        {
            return page.Error;
        }

        int? afterOrdinal = null;
        if (page.Value.After is { } after)
        {
            if (!int.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return EvaluationAccess.InvalidCursor();
            }

            afterOrdinal = parsed;
        }

        var query = db.Cases.AsNoTracking().Where(c => c.DatasetId == datasetId && c.DatasetVersion == version);
        if (afterOrdinal is { } last)
        {
            query = query.Where(c => c.Ordinal > last);
        }

        var rows = await query.OrderBy(c => c.Ordinal).Take(page.Value.Limit + 1).ToListAsync(cancellationToken);
        var views = rows.Select(c => new CaseView(
                c.Id,
                c.ExternalId,
                c.Ordinal,
                JsonNode.Parse(c.InputJson),
                JsonNode.Parse(c.ExpectedJson),
                c.MetadataJson is null ? null : JsonNode.Parse(c.MetadataJson)))
            .ToList();
        return Pagination.Page(views, page.Value.Limit, v => v.Ordinal.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The Dataset when it exists and its Tenant is in the caller's grant; otherwise the same 404 either way.</summary>
    private async Task<Result<Dataset>> FindAsync(EvaluationDbContext db, Guid datasetId, CancellationToken cancellationToken)
    {
        var dataset = await db.Datasets.AsNoTracking().FirstOrDefaultAsync(d => d.Id == datasetId, cancellationToken);
        if (dataset is null || access.CheckTenant(dataset.TenantId).IsFailure)
        {
            return DatasetNotFound();
        }

        return dataset;
    }

    private async Task<Result> RequireTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var granted = access.CheckTenant(tenantId);
        if (granted.IsFailure)
        {
            return granted;
        }

        return await tenants.ExistsAsync(tenantId, cancellationToken)
            ? Result.Success()
            : new Error(ErrorCode.NotFound, "The resource was not found.");
    }

    /// <summary>Reads at most <paramref name="limit"/> bytes; null when the body is longer.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream body, int limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static bool IsJsonl(string? contentType)
        => contentType is not null
            && MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            && UploadMediaTypes.Contains(parsed.MediaType, StringComparer.OrdinalIgnoreCase);

    private static Error TooLarge()
        => new(ErrorCode.PayloadTooLarge, $"A Dataset Version upload must not exceed {DatasetJsonl.MaxBytes} bytes (10 MiB).");

    private static Error DatasetNotFound() => new(ErrorCode.NotFound, "The dataset was not found.");

    private static Error VersionNotFound() => new(ErrorCode.NotFound, "The dataset version was not found.");

    private static DatasetView ToView(Dataset dataset)
        => new(dataset.Id, dataset.TenantId, dataset.Name, dataset.TaskKind, dataset.LatestVersion, dataset.CreatedAt);

    private static DatasetVersionView ToView(DatasetVersion version)
        => new(version.DatasetId, version.Version, version.CaseCount, version.ContentSha256, JsonNode.Parse(version.ScoringSchemaJson), version.CreatedAt, version.CreatedByAdminId);
}
