using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ModelSwitch.Registry.Domain;

namespace ModelSwitch.Registry.Infrastructure;

internal sealed class RegistryDbContext(DbContextOptions<RegistryDbContext> options) : DbContext(options)
{
    public const string Schema = "registry";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<Provider> Providers => Set<Provider>();

    public DbSet<ProviderModel> ProviderModels => Set<ProviderModel>();

    public DbSet<ModelPrice> ModelPrices => Set<ModelPrice>();

    public DbSet<ProviderHealthSnapshot> ProviderHealthSnapshots => Set<ProviderHealthSnapshot>();

    public DbSet<ProviderSecret> ProviderSecrets => Set<ProviderSecret>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ProviderHealthSnapshot>(snapshot =>
        {
            snapshot.ToTable("provider_health_snapshot", t => t.HasCheckConstraint("ck_provider_health_snapshot_circuit_state", "circuit_state in ('closed', 'half_open', 'open')"));
            snapshot.HasKey(x => new { x.InstanceId, x.ProviderModelId });
            snapshot.Property(x => x.InstanceId).HasColumnName("instance_id");
            snapshot.Property(x => x.ProviderModelId).HasColumnName("provider_model_id");
            snapshot.Property(x => x.CircuitState).HasColumnName("circuit_state").IsRequired();
            snapshot.Property(x => x.WindowFailureRate).HasColumnName("window_failure_rate");
            snapshot.Property(x => x.WindowCalls).HasColumnName("window_calls");
            snapshot.Property(x => x.CapturedAt).HasColumnName("captured_at");
        });

        modelBuilder.Entity<ProviderSecret>(secret =>
        {
            secret.ToTable("provider_secret", t =>
            {
                t.HasCheckConstraint("ck_provider_secret_nonce_length", "octet_length(nonce) = 12");
                t.HasCheckConstraint("ck_provider_secret_tag_length", "octet_length(tag) = 16");
            });
            secret.HasKey(x => x.Id);
            secret.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            secret.Property(x => x.ProviderId).HasColumnName("provider_id");
            secret.Property(x => x.Ciphertext).HasColumnName("ciphertext").HasColumnType("bytea").IsRequired();
            secret.Property(x => x.Nonce).HasColumnName("nonce").HasColumnType("bytea").IsRequired();
            secret.Property(x => x.Tag).HasColumnName("tag").HasColumnType("bytea").IsRequired();
            secret.Property(x => x.KeyVersion).HasColumnName("key_version");
            secret.Property(x => x.CreatedAt).HasColumnName("created_at");
            secret.HasIndex(x => x.ProviderId).HasDatabaseName("ix_provider_secret_provider_id");
            secret.HasOne<Provider>().WithMany().HasForeignKey(x => x.ProviderId).HasConstraintName("fk_provider_secret_provider_id").OnDelete(DeleteBehavior.Cascade);
        });

        var adapterType = new ValueConverter<AdapterType, string>(
            type => type == AdapterType.OpenAiCompatible ? "openai_compatible" : "ollama",
            text => text == "openai_compatible" ? AdapterType.OpenAiCompatible : AdapterType.Ollama);

        modelBuilder.Entity<Provider>(provider =>
        {
            provider.ToTable("provider", t => t.HasCheckConstraint("ck_provider_adapter_type", "adapter_type in ('openai_compatible', 'ollama')"));
            provider.HasKey(x => x.Id);
            provider.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            provider.Property(x => x.Name).HasColumnName("name").IsRequired();
            provider.Property(x => x.AdapterType).HasColumnName("adapter_type").HasConversion(adapterType).IsRequired();
            provider.Property(x => x.Endpoint).HasColumnName("endpoint").IsRequired();
            provider.Property(x => x.CredentialReference).HasColumnName("credential_reference");
            provider.Property(x => x.RequestTimeoutMs).HasColumnName("request_timeout_ms");
            provider.Property(x => x.Enabled).HasColumnName("enabled");
            provider.Property(x => x.CreatedAt).HasColumnName("created_at");
            provider.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            provider.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_provider_name");
        });

        modelBuilder.Entity<ProviderModel>(model =>
        {
            model.ToTable("provider_model");
            model.HasKey(x => x.Id);
            model.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            model.Property(x => x.ProviderId).HasColumnName("provider_id");
            model.Property(x => x.ProviderModelIdentifier).HasColumnName("provider_model_id").IsRequired();
            model.Property(x => x.DisplayName).HasColumnName("display_name").IsRequired();
            model.Property(x => x.CapabilitiesJson).HasColumnName("capabilities").HasColumnType("jsonb").IsRequired();
            model.Property(x => x.Enabled).HasColumnName("enabled");
            model.Property(x => x.CreatedAt).HasColumnName("created_at");
            model.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            model.HasIndex(x => new { x.ProviderId, x.ProviderModelIdentifier }).IsUnique().HasDatabaseName("ux_provider_model_provider_id_provider_model_id");
            model.HasOne<Provider>().WithMany().HasForeignKey(x => x.ProviderId).HasConstraintName("fk_provider_model_provider_id").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ModelPrice>(price =>
        {
            price.ToTable("model_price", t =>
            {
                t.HasCheckConstraint("ck_model_price_currency", "currency = 'USD'");
                t.HasCheckConstraint("ck_model_price_non_negative", "input_per_million >= 0 and output_per_million >= 0 and (cached_input_per_million is null or cached_input_per_million >= 0)");
            });
            price.HasKey(x => x.Id);
            price.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            price.Property(x => x.ProviderModelId).HasColumnName("provider_model_id");
            price.Property(x => x.InputPerMillion).HasColumnName("input_per_million").HasColumnType("numeric(20,10)");
            price.Property(x => x.CachedInputPerMillion).HasColumnName("cached_input_per_million").HasColumnType("numeric(20,10)");
            price.Property(x => x.OutputPerMillion).HasColumnName("output_per_million").HasColumnType("numeric(20,10)");
            price.Property(x => x.Currency).HasColumnName("currency").HasColumnType("character(3)").IsRequired();
            price.Property(x => x.EffectiveFrom).HasColumnName("effective_from");
            price.Property(x => x.CreatedAt).HasColumnName("created_at");
            price.Property(x => x.CreatedByAdminId).HasColumnName("created_by_admin_id");
            price.HasIndex(x => new { x.ProviderModelId, x.EffectiveFrom }).IsUnique().HasDatabaseName("ux_model_price_provider_model_id_effective_from");
            price.HasOne<ProviderModel>().WithMany().HasForeignKey(x => x.ProviderModelId).HasConstraintName("fk_model_price_provider_model_id").OnDelete(DeleteBehavior.Restrict);
        });
    }
}
