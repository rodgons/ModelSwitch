using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Registry.Domain;
using ModelSwitch.Registry.Infrastructure;
using ModelSwitch.Registry.Infrastructure.Secrets;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Pagination;

namespace ModelSwitch.Registry.Application;

/// <summary>A Provider credential on a write. Exactly one member is set. The secret never appears in <see cref="ToString"/>.</summary>
internal sealed record CredentialRequest(string? Secret, string? EnvVar)
{
    public override string ToString() => $"CredentialRequest {{ Secret = {(Secret is null ? "null" : "[redacted]")}, EnvVar = {EnvVar} }}";
}

internal sealed record CreateProviderRequest(string? Name, string? AdapterType, string? Endpoint, int? RequestTimeoutMs, bool? Enabled, CredentialRequest? Credential);

/// <summary>A null field is left unchanged. The name and Adapter type are fixed at creation: Policy Versions refer to the name.</summary>
internal sealed record UpdateProviderRequest(string? Endpoint, int? RequestTimeoutMs, bool? Enabled, CredentialRequest? Credential);

/// <summary>What an administrator may learn about a credential: the kind and whether it is set, never a fragment of the secret.</summary>
internal sealed record CredentialView(string Kind, bool Configured, DateTimeOffset? UpdatedAt);

internal sealed record ProviderView(
    Guid Id,
    string Name,
    string AdapterType,
    string Endpoint,
    CredentialView Credential,
    int RequestTimeoutMs,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record ConnectivityFailureView(string Class, string Code, int? HttpStatus);

internal sealed record ConnectivityView(bool Reachable, long LatencyMs, ConnectivityFailureView? Failure);

/// <summary>
/// Provider management (FR-10; Owner writes). Every write is one transaction holding the change, its Audit Records and the
/// config version bump. The endpoint is validated by the outbound destination policy on save (security.md section 5).
/// </summary>
internal sealed class ProviderAdminService(
    IDbContextFactory<RegistryDbContext> contexts,
    IOutboundDestinationPolicy outbound,
    ProviderSecretStore secrets,
    IAdapterFactory adapters,
    IAuditWriter audit,
    IGuidGenerator ids,
    TimeProvider time)
{
    private static readonly TimeSpan _connectivityTimeout = TimeSpan.FromSeconds(5);

    public async Task<Result<PagedResult<ProviderView>>> ListAsync(string? limit, string? cursor, CancellationToken cancellationToken)
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
        var query = db.Providers.AsNoTracking();
        if (after is { } last)
        {
            query = query.Where(p => p.Id.CompareTo(last) > 0);
        }

        var rows = await query.OrderBy(p => p.Id).Take(page.Value.Limit + 1).ToListAsync(cancellationToken);
        var secretDates = await SecretDatesAsync(db, rows, cancellationToken);
        var views = rows.Select(p => ToView(p, secretDates)).ToList();
        return Pagination.Page(views, page.Value.Limit, v => v.Id.ToString("D", CultureInfo.InvariantCulture));
    }

    public async Task<Result<ProviderView>> GetAsync(Guid providerId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var provider = await db.Providers.AsNoTracking().FirstOrDefaultAsync(p => p.Id == providerId, cancellationToken);
        return provider is null ? NotFound() : ToView(provider, await SecretDatesAsync(db, [provider], cancellationToken));
    }

    public async Task<Result<ProviderView>> CreateAsync(CreateProviderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        var name = AdminRules.ProviderName(request.Name, errors);
        if (!AdminRules.TryParseAdapterType(request.AdapterType, out var adapterType))
        {
            errors["adapterType"] = ["The adapter type must be one of: openai_compatible, ollama."];
        }

        var timeout = AdminRules.RequestTimeout(request.RequestTimeoutMs, errors);
        var reference = AdminRules.Credential(request.Credential, errors, out var hasSecret);
        await ValidateEndpointAsync(request.Endpoint, errors, cancellationToken);
        if (hasSecret && secrets.Validate(request.Credential!.Secret) is { } secretProblem)
        {
            if (secretProblem.Code == ErrorCode.InternalError)
            {
                return secretProblem;
            }

            errors["credential.secret"] = [secretProblem.Message];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The provider is invalid.", errors);
        }

        var now = time.GetUtcNow();
        var provider = new Provider
        {
            Id = ids.NewGuid(),
            Name = name!,
            AdapterType = adapterType,
            Endpoint = request.Endpoint!.Trim(),
            CredentialReference = reference,
            RequestTimeoutMs = timeout ?? AdminRules.DefaultTimeoutMs,
            Enabled = request.Enabled ?? true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Providers.Add(provider);
        var entries = new List<AuditEntry> { new(RegistryAuditActions.ProviderCreated, "provider", provider.Id.ToString("D", CultureInfo.InvariantCulture)) { After = Summarise(provider) } };
        if (hasSecret)
        {
            await secrets.ApplyAsync(db, provider, request.Credential!.Secret!, cancellationToken);
            entries.Add(secrets.SecretAuditEntry(provider));
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (ConfigWrite.IsUniqueViolation(exception))
        {
            return new Error(ErrorCode.Conflict, "A provider with this name already exists.");
        }

        await ConfigWrite.CommitAsync(db, transaction, audit, entries, cancellationToken);
        return ToView(provider, hasSecret ? new Dictionary<Guid, DateTimeOffset> { [SecretId(provider)] = now } : []);
    }

    public async Task<Result<ProviderView>> UpdateAsync(Guid providerId, UpdateProviderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        if (request is { Endpoint: null, RequestTimeoutMs: null, Enabled: null, Credential: null })
        {
            errors["body"] = ["Give at least one of: endpoint, requestTimeoutMs, enabled, credential."];
        }

        var timeout = AdminRules.RequestTimeout(request.RequestTimeoutMs, errors);
        var reference = AdminRules.Credential(request.Credential, errors, out var hasSecret);
        if (request.Endpoint is not null)
        {
            await ValidateEndpointAsync(request.Endpoint, errors, cancellationToken);
        }

        if (hasSecret && secrets.Validate(request.Credential!.Secret) is { } secretProblem)
        {
            if (secretProblem.Code == ErrorCode.InternalError)
            {
                return secretProblem;
            }

            errors["credential.secret"] = [secretProblem.Message];
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var provider = await db.Providers.FirstOrDefaultAsync(p => p.Id == providerId, cancellationToken);
        if (provider is null)
        {
            return NotFound();
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The provider is invalid.", errors);
        }

        var before = Summarise(provider);
        if (request.Endpoint is not null)
        {
            provider.Endpoint = request.Endpoint.Trim();
        }

        provider.RequestTimeoutMs = timeout ?? provider.RequestTimeoutMs;
        provider.Enabled = request.Enabled ?? provider.Enabled;
        if (reference is not null)
        {
            await ProviderSecretStore.RemoveReferencedRowAsync(db, provider.CredentialReference, cancellationToken);
            provider.CredentialReference = reference;
        }

        if (hasSecret)
        {
            await secrets.ApplyAsync(db, provider, request.Credential!.Secret!, cancellationToken);
        }

        var after = Summarise(provider);
        var entries = new List<AuditEntry>();
        if (!before.SequenceEqual(after) || reference is not null)
        {
            provider.UpdatedAt = time.GetUtcNow();
            entries.Add(new AuditEntry(RegistryAuditActions.ProviderModified, "provider", providerId.ToString("D", CultureInfo.InvariantCulture)) { Before = before, After = after });
        }

        if (hasSecret)
        {
            entries.Add(secrets.SecretAuditEntry(provider));
        }

        await db.SaveChangesAsync(cancellationToken);
        await ConfigWrite.CommitAsync(db, transaction, audit, entries, cancellationToken);
        return ToView(provider, await SecretDatesAsync(db, [provider], cancellationToken));
    }

    /// <summary>Calls the Adapter's <c>CheckAsync</c> with the Provider's own credential. Always a result; unreachable is not an error.</summary>
    public async Task<Result<ConnectivityView>> CheckConnectivityAsync(Guid providerId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var provider = await db.Providers.AsNoTracking().FirstOrDefaultAsync(p => p.Id == providerId, cancellationToken);
        if (provider is null)
        {
            return NotFound();
        }

        if (!Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var endpoint))
        {
            return Unreachable("permanent", "invalid_endpoint", 0);
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Min(provider.RequestTimeoutMs, _connectivityTimeout.TotalMilliseconds));
        var target = new ProviderModelTarget(Guid.Empty, provider.Id, provider.Name, AdminRules.ToWire(provider.AdapterType), endpoint, provider.CredentialReference, timeout, "connectivity-check");
        var binding = await adapters.BindAsync(target, cancellationToken);
        if (binding.IsFailure)
        {
            return Unreachable("permanent", "adapter_unavailable", 0);
        }

        var started = time.GetTimestamp();
        var result = await binding.Value.Adapter.CheckAsync(binding.Value.Context, cancellationToken);
        var latency = (long)time.GetElapsedTime(started).TotalMilliseconds;
        return new ConnectivityView(
            result.Reachable,
            latency,
            result.Failure is { } failure ? new ConnectivityFailureView(ClassName(failure.Class), failure.Code, failure.HttpStatus) : null);
    }

    private static string ClassName(FailureClass failureClass) => failureClass switch
    {
        FailureClass.Transient => "transient",
        FailureClass.Permanent => "permanent",
        _ => "ambiguous",
    };

    private static ConnectivityView Unreachable(string failureClass, string code, long latencyMs)
        => new(false, latencyMs, new ConnectivityFailureView(failureClass, code, null));

    private async Task ValidateEndpointAsync(string? endpoint, Dictionary<string, string[]> errors, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            errors["endpoint"] = ["The endpoint is required."];
            return;
        }

        var check = await outbound.ValidateEndpointAsync(endpoint, cancellationToken);
        if (check.IsFailure)
        {
            errors["endpoint"] = check.Error.FieldErrors is { } fields && fields.TryGetValue("endpoint", out var messages) ? messages : [check.Error.Message];
        }
    }

    private static async Task<Dictionary<Guid, DateTimeOffset>> SecretDatesAsync(RegistryDbContext db, IEnumerable<Provider> providers, CancellationToken cancellationToken)
    {
        var secretIds = providers
            .Select(p => p.CredentialReference)
            .Where(r => r is not null)
            .Select(r => ProviderSecretStore.TryParseSecretId(r!, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList();
        return secretIds.Count == 0
            ? []
            : await db.ProviderSecrets.AsNoTracking().Where(s => secretIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.CreatedAt, cancellationToken);
    }

    private static Guid SecretId(Provider provider)
        => ProviderSecretStore.TryParseSecretId(provider.CredentialReference!, out var id) ? id : Guid.Empty;

    private static ProviderView ToView(Provider provider, Dictionary<Guid, DateTimeOffset> secretDates)
    {
        var reference = provider.CredentialReference;
        var credential = string.IsNullOrWhiteSpace(reference)
            ? new CredentialView("none", false, null)
            : ProviderSecretStore.TryParseSecretId(reference, out var secretId)
                ? new CredentialView("db", true, secretDates.TryGetValue(secretId, out var updated) ? updated : null)
                : new CredentialView("env", true, null);
        return new ProviderView(
            provider.Id,
            provider.Name,
            AdminRules.ToWire(provider.AdapterType),
            provider.Endpoint,
            credential,
            provider.RequestTimeoutMs,
            provider.Enabled,
            provider.CreatedAt,
            provider.UpdatedAt);
    }

    // Audit summaries: the credential is reduced to its kind (and an env var's name is not repeated there).
    private static Dictionary<string, object?> Summarise(Provider provider) => new()
    {
        ["name"] = provider.Name,
        ["adapterType"] = AdminRules.ToWire(provider.AdapterType),
        ["endpoint"] = provider.Endpoint,
        ["requestTimeoutMs"] = provider.RequestTimeoutMs,
        ["enabled"] = provider.Enabled,
        ["credentialKind"] = string.IsNullOrWhiteSpace(provider.CredentialReference) ? "none" : ProviderSecretStore.TryParseSecretId(provider.CredentialReference, out _) ? "db" : "env",
    };

    private static Error NotFound() => new(ErrorCode.NotFound, "The provider was not found.");
}
