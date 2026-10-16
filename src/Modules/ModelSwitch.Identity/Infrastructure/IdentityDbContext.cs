using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.Identity.Domain;

namespace ModelSwitch.Identity.Infrastructure;

internal sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public const string Schema = "identity";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantLimitsRow> TenantLimits => Set<TenantLimitsRow>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<Administrator> Administrators => Set<Administrator>();

    public DbSet<AdministratorTenantGrant> AdministratorTenantGrants => Set<AdministratorTenantGrant>();

    public DbSet<AdministratorSession> AdministratorSessions => Set<AdministratorSession>();

    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    private static AdministratorRole ParseRole(string text) => AdministratorRoles.TryParse(text, out var parsed) ? parsed : AdministratorRole.Viewer;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);

        var tenantStatus = new ValueConverter<TenantStatus, string>(
            status => status == TenantStatus.Active ? "active" : "suspended",
            text => text == "active" ? TenantStatus.Active : TenantStatus.Suspended);

        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.ToTable("tenant", t => t.HasCheckConstraint("ck_tenant_status", "status in ('active', 'suspended')"));
            tenant.HasKey(x => x.Id);
            tenant.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            tenant.Property(x => x.Name).HasColumnName("name").IsRequired();
            tenant.Property(x => x.Status).HasColumnName("status").HasConversion(tenantStatus).IsRequired();
            tenant.Property(x => x.CreatedAt).HasColumnName("created_at");
            tenant.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            tenant.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_tenant_name");
        });

        modelBuilder.Entity<TenantLimitsRow>(limits =>
        {
            limits.ToTable("tenant_limits", t =>
            {
                t.HasCheckConstraint("ck_tenant_limits_requests_per_minute", "requests_per_minute is null or requests_per_minute > 0");
                t.HasCheckConstraint("ck_tenant_limits_max_concurrent", "max_concurrent is null or max_concurrent > 0");
                t.HasCheckConstraint("ck_tenant_limits_budget_period", "budget_period is null or budget_period = 'month'");
            });
            limits.HasKey(x => x.TenantId);
            limits.Property(x => x.TenantId).HasColumnName("tenant_id").ValueGeneratedNever();
            limits.Property(x => x.RequestsPerMinute).HasColumnName("requests_per_minute");
            limits.Property(x => x.MaxConcurrent).HasColumnName("max_concurrent");
            limits.Property(x => x.MaxRequestCost).HasColumnName("max_request_cost").HasColumnType("numeric(20,10)");
            limits.Property(x => x.BudgetAmount).HasColumnName("budget_amount").HasColumnType("numeric(20,10)");
            limits.Property(x => x.BudgetPeriod).HasColumnName("budget_period");
            limits.Property(x => x.AllowedAliases).HasColumnName("allowed_aliases").HasColumnType("text[]").HasDefaultValueSql("'{*}'").IsRequired();
            limits.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            limits.HasOne<Tenant>().WithOne().HasForeignKey<TenantLimitsRow>(x => x.TenantId).HasConstraintName("fk_tenant_limits_tenant_id").OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApiKey>(key =>
        {
            key.ToTable("api_key", t => t.HasCheckConstraint("ck_api_key_key_hash_length", "octet_length(key_hash) = 32"));
            key.HasKey(x => x.Id);
            key.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            key.Property(x => x.TenantId).HasColumnName("tenant_id");
            key.Property(x => x.Name).HasColumnName("name").IsRequired();
            key.Property(x => x.KeyPrefix).HasColumnName("key_prefix").HasColumnType("character(8)").IsRequired();
            key.Property(x => x.KeyHash).HasColumnName("key_hash").HasColumnType("bytea").IsRequired();
            key.Property(x => x.Scopes).HasColumnName("scopes").HasColumnType("text[]").IsRequired();
            key.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            key.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            key.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
            key.Property(x => x.CreatedAt).HasColumnName("created_at");
            key.Property(x => x.CreatedByAdminId).HasColumnName("created_by_admin_id");
            key.HasIndex(x => x.KeyPrefix).IsUnique().HasDatabaseName("ux_api_key_key_prefix");
            key.HasIndex(x => new { x.TenantId, x.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_api_key_tenant_id_created_at");
            key.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).HasConstraintName("fk_api_key_tenant_id").OnDelete(DeleteBehavior.Restrict);
        });

        var role = new ValueConverter<AdministratorRole, string>(
            value => AdministratorRoles.ToWire(value),
            text => ParseRole(text));

        modelBuilder.Entity<Administrator>(admin =>
        {
            admin.ToTable("administrator", t =>
            {
                t.HasCheckConstraint("ck_administrator_role", "role in ('owner', 'operator', 'viewer')");
                t.HasCheckConstraint("ck_administrator_owner_all_tenants", "role <> 'owner' or all_tenants");
            });
            admin.HasKey(x => x.Id);
            admin.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            admin.Property(x => x.Email).HasColumnName("email").IsRequired();
            admin.Property(x => x.PasswordHash).HasColumnName("password_hash").IsRequired();
            admin.Property(x => x.Role).HasColumnName("role").HasConversion(role).IsRequired();
            admin.Property(x => x.AllTenants).HasColumnName("all_tenants");
            admin.Property(x => x.Active).HasColumnName("active");
            admin.Property(x => x.FailedSignInCount).HasColumnName("failed_sign_in_count");
            admin.Property(x => x.FailedSignInWindowStartedAt).HasColumnName("failed_sign_in_window_started_at");
            admin.Property(x => x.LockedUntil).HasColumnName("locked_until");
            admin.Property(x => x.LastSignedInAt).HasColumnName("last_signed_in_at");
            admin.Property(x => x.CreatedAt).HasColumnName("created_at");
            admin.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            admin.Ignore(x => x.Grant);
            admin.HasIndex(x => x.Email).IsUnique().HasDatabaseName("ux_administrator_email");
            admin.HasMany(x => x.Grants).WithOne().HasForeignKey(g => g.AdministratorId).HasConstraintName("fk_administrator_tenant_grant_administrator_id").OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AdministratorTenantGrant>(grant =>
        {
            grant.ToTable("administrator_tenant_grant");
            grant.HasKey(x => new { x.AdministratorId, x.TenantId });
            grant.Property(x => x.AdministratorId).HasColumnName("administrator_id");
            grant.Property(x => x.TenantId).HasColumnName("tenant_id");
            grant.HasIndex(x => x.TenantId).HasDatabaseName("ix_administrator_tenant_grant_tenant_id");
            grant.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).HasConstraintName("fk_administrator_tenant_grant_tenant_id").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AdministratorSession>(session =>
        {
            session.ToTable("administrator_session");
            session.HasKey(x => x.Id);
            session.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            session.Property(x => x.AdministratorId).HasColumnName("administrator_id");
            session.Property(x => x.CreatedAt).HasColumnName("created_at");
            session.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            session.Property(x => x.AbsoluteExpiresAt).HasColumnName("absolute_expires_at");
            session.HasIndex(x => x.AdministratorId).HasDatabaseName("ix_administrator_session_administrator_id");
            session.HasOne<Administrator>().WithMany().HasForeignKey(x => x.AdministratorId).HasConstraintName("fk_administrator_session_administrator_id").OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditRecord>(record =>
        {
            record.ToTable("audit_record");
            record.HasKey(x => x.Id);
            record.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            record.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            record.Property(x => x.AdministratorId).HasColumnName("administrator_id");
            record.Property(x => x.Action).HasColumnName("action").IsRequired();
            record.Property(x => x.TenantId).HasColumnName("tenant_id");
            record.Property(x => x.TargetType).HasColumnName("target_type").IsRequired();
            record.Property(x => x.TargetId).HasColumnName("target_id");
            record.Property(x => x.Details).HasColumnName("details").HasColumnType("jsonb").IsRequired();
            record.Property(x => x.SourceIp).HasColumnName("source_ip").HasColumnType("inet");
            record.Property(x => x.RequestId).HasColumnName("request_id");
            record.Property(x => x.TraceId).HasColumnName("trace_id");
            record.HasIndex(x => x.OccurredAt).IsDescending().HasDatabaseName("ix_audit_record_occurred_at");
            record.HasIndex(x => new { x.TenantId, x.OccurredAt }).IsDescending(false, true).HasDatabaseName("ix_audit_record_tenant_id_occurred_at");
        });

        modelBuilder.Entity<DataProtectionKey>(key =>
        {
            key.ToTable("data_protection_key");
            key.HasKey(x => x.Id);
            key.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            key.Property(x => x.FriendlyName).HasColumnName("friendly_name");
            key.Property(x => x.Xml).HasColumnName("xml").IsRequired();
            key.Property(x => x.CreatedAt).HasColumnName("created_at");
        });
    }
}

/// <summary>An ASP.NET Core Data Protection key ring entry, shared by every api instance so cookies and antiforgery tokens work across them.</summary>
internal sealed class DataProtectionKey
{
    public Guid Id { get; init; }

    public string? FriendlyName { get; init; }

    public required string Xml { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
