using Microsoft.EntityFrameworkCore;
using ModelSwitch.Routing.Domain;

namespace ModelSwitch.Routing.Infrastructure;

internal sealed class RoutingDbContext(DbContextOptions<RoutingDbContext> options) : DbContext(options)
{
    public const string Schema = "routing";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<RoutingPolicy> Policies => Set<RoutingPolicy>();

    public DbSet<PolicyVersion> PolicyVersions => Set<PolicyVersion>();

    public DbSet<ModelAlias> Aliases => Set<ModelAlias>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<RoutingPolicy>(policy =>
        {
            policy.ToTable("routing_policy", t => t.HasCheckConstraint("ck_routing_policy_latest_version", "latest_version >= 1"));
            policy.HasKey(x => x.Id);
            policy.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            policy.Property(x => x.Name).HasColumnName("name").IsRequired();
            policy.Property(x => x.LatestVersion).HasColumnName("latest_version");
            policy.Property(x => x.CreatedAt).HasColumnName("created_at");
            policy.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_routing_policy_name");
        });

        modelBuilder.Entity<PolicyVersion>(version =>
        {
            version.ToTable("routing_policy_version", t => t.HasCheckConstraint("ck_routing_policy_version_version", "version >= 1"));
            version.HasKey(x => new { x.PolicyId, x.Version });
            version.Property(x => x.PolicyId).HasColumnName("policy_id");
            version.Property(x => x.Version).HasColumnName("version");
            version.Property(x => x.DefinitionJson).HasColumnName("definition").HasColumnType("jsonb").IsRequired();
            version.Property(x => x.ResolvedCandidatesJson).HasColumnName("resolved_candidates").HasColumnType("jsonb").IsRequired();
            version.Property(x => x.CreatedAt).HasColumnName("created_at");
            version.Property(x => x.CreatedByAdminId).HasColumnName("created_by_admin_id");
            version.HasOne<RoutingPolicy>().WithMany().HasForeignKey(x => x.PolicyId).HasConstraintName("fk_routing_policy_version_policy_id").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ModelAlias>(alias =>
        {
            alias.ToTable("model_alias", t => t.HasCheckConstraint("ck_model_alias_alias", "alias ~ '^[a-z0-9][a-z0-9._-]{0,63}$'"));
            alias.HasKey(x => x.Alias);
            alias.Property(x => x.Alias).HasColumnName("alias");
            alias.Property(x => x.PolicyId).HasColumnName("policy_id");
            alias.Property(x => x.Description).HasColumnName("description");
            alias.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            alias.HasOne<RoutingPolicy>().WithMany().HasForeignKey(x => x.PolicyId).HasConstraintName("fk_model_alias_policy_id").OnDelete(DeleteBehavior.Restrict);
        });
    }
}
