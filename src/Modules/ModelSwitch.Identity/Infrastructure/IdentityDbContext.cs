using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ModelSwitch.Identity.Domain;

namespace ModelSwitch.Identity.Infrastructure;

internal sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public const string Schema = "identity";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

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
    }
}
