using Microsoft.EntityFrameworkCore;
using ModelSwitch.Usage.Domain;

namespace ModelSwitch.Usage.Infrastructure;

/// <summary>
/// The <c>usage</c> schema. <c>inference_request</c> and <c>provider_attempt</c> are range-partitioned by month on
/// <c>started_at</c>, which EF Core cannot create: the migration issues that DDL as raw SQL and this model only describes
/// the columns (data-model.md, "usage"). Both primary keys therefore include <c>started_at</c>, as PostgreSQL requires.
/// </summary>
internal sealed class UsageDbContext(DbContextOptions<UsageDbContext> options) : DbContext(options)
{
    public const string Schema = "usage";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<InferenceRequestRow> InferenceRequests => Set<InferenceRequestRow>();

    public DbSet<ProviderAttemptRow> ProviderAttempts => Set<ProviderAttemptRow>();

    public DbSet<BudgetRow> Budgets => Set<BudgetRow>();

    public DbSet<BudgetPeriodRow> BudgetPeriods => Set<BudgetPeriodRow>();

    public DbSet<ReservationRow> Reservations => Set<ReservationRow>();

    public DbSet<UsageRecordRow> UsageRecords => Set<UsageRecordRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<BudgetRow>(budget =>
        {
            budget.ToTable("budget", t => t.HasCheckConstraint("ck_budget_limit", "limit_amount >= 0"));
            budget.HasKey(x => x.TenantId);
            budget.Property(x => x.TenantId).HasColumnName("tenant_id").ValueGeneratedNever();
            budget.Property(x => x.LimitAmount).HasColumnName("limit_amount").HasColumnType("numeric(20,10)");
            budget.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)").IsRequired();
            budget.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<BudgetPeriodRow>(period =>
        {
            period.ToTable("budget_period", t => t.HasCheckConstraint("ck_budget_period_reserved", "reserved_amount >= 0"));
            period.HasKey(x => new { x.TenantId, x.PeriodStart });
            period.Property(x => x.TenantId).HasColumnName("tenant_id");
            period.Property(x => x.PeriodStart).HasColumnName("period_start");
            period.Property(x => x.LimitAmount).HasColumnName("limit_amount").HasColumnType("numeric(20,10)");
            period.Property(x => x.ReservedAmount).HasColumnName("reserved_amount").HasColumnType("numeric(20,10)").HasDefaultValue(0m);
            period.Property(x => x.SpentAmount).HasColumnName("spent_amount").HasColumnType("numeric(20,10)").HasDefaultValue(0m);
            period.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)").IsRequired();
        });

        modelBuilder.Entity<ReservationRow>(reservation =>
        {
            reservation.ToTable("reservation", t => t.HasCheckConstraint("ck_reservation_status", "status in ('held', 'settled', 'expired')"));
            reservation.HasKey(x => x.Id);
            reservation.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            reservation.Property(x => x.InferenceRequestId).HasColumnName("inference_request_id");
            reservation.Property(x => x.TenantId).HasColumnName("tenant_id");
            reservation.Property(x => x.PeriodStart).HasColumnName("period_start");
            reservation.Property(x => x.Amount).HasColumnName("amount").HasColumnType("numeric(20,10)");
            reservation.Property(x => x.Status).HasColumnName("status").IsRequired();
            reservation.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            reservation.Property(x => x.CreatedAt).HasColumnName("created_at");
            reservation.Property(x => x.SettledAt).HasColumnName("settled_at");
            reservation.HasIndex(x => x.InferenceRequestId).IsUnique().HasDatabaseName("ux_reservation_inference_request_id");
            reservation.HasIndex(x => new { x.Status, x.ExpiresAt }).HasFilter("status = 'held'").HasDatabaseName("ix_reservation_status_expires_at");
        });

        modelBuilder.Entity<UsageRecordRow>(record =>
        {
            record.ToTable("usage_record", t => t.HasCheckConstraint("ck_usage_record_usage_source", "usage_source in ('reported', 'estimated', 'unavailable')"));
            record.HasKey(x => x.Id);
            record.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            record.Property(x => x.InferenceRequestId).HasColumnName("inference_request_id");
            record.Property(x => x.ProviderAttemptId).HasColumnName("provider_attempt_id");
            record.Property(x => x.TenantId).HasColumnName("tenant_id");
            record.Property(x => x.ProviderModelId).HasColumnName("provider_model_id");
            record.Property(x => x.ModelPriceId).HasColumnName("model_price_id");
            record.Property(x => x.InputTokens).HasColumnName("input_tokens");
            record.Property(x => x.CachedInputTokens).HasColumnName("cached_input_tokens");
            record.Property(x => x.OutputTokens).HasColumnName("output_tokens");
            record.Property(x => x.UsageSource).HasColumnName("usage_source").IsRequired();
            record.Property(x => x.EstimateMethod).HasColumnName("estimate_method");
            record.Property(x => x.InputCost).HasColumnName("input_cost").HasColumnType("numeric(20,10)");
            record.Property(x => x.OutputCost).HasColumnName("output_cost").HasColumnType("numeric(20,10)");
            record.Property(x => x.TotalCost).HasColumnName("total_cost").HasColumnType("numeric(20,10)");
            record.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)").IsRequired();
            record.Property(x => x.RecordedAt).HasColumnName("recorded_at");
            record.HasIndex(x => x.ProviderAttemptId).IsUnique().HasDatabaseName("ux_usage_record_provider_attempt_id");
            record.HasIndex(x => new { x.TenantId, x.RecordedAt }).HasDatabaseName("ix_usage_record_tenant_id_recorded_at");
            record.HasIndex(x => x.InferenceRequestId).HasDatabaseName("ix_usage_record_inference_request_id");
        });

        modelBuilder.Entity<InferenceRequestRow>(request =>
        {
            request.ToTable("inference_request", t =>
            {
                t.HasCheckConstraint("ck_inference_request_origin", "origin in ('api', 'evaluation')");
                t.HasCheckConstraint(
                    "ck_inference_request_origin_ids",
                    "(origin = 'api' and api_key_id is not null and evaluation_run_id is null) or (origin = 'evaluation' and api_key_id is null and evaluation_run_id is not null)");
                t.HasCheckConstraint("ck_inference_request_status", "status in ('running', 'succeeded', 'failed', 'cancelled', 'abandoned')");
                t.HasCheckConstraint("ck_inference_request_usage_status", "usage_status in ('reported', 'estimated', 'partial', 'unavailable')");
            });
            request.HasKey(x => new { x.Id, x.StartedAt });
            request.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            request.Property(x => x.StartedAt).HasColumnName("started_at");
            request.Property(x => x.TenantId).HasColumnName("tenant_id");
            request.Property(x => x.Origin).HasColumnName("origin").IsRequired();
            request.Property(x => x.ApiKeyId).HasColumnName("api_key_id");
            request.Property(x => x.EvaluationRunId).HasColumnName("evaluation_run_id");
            request.Property(x => x.ClientRequestId).HasColumnName("client_request_id");
            request.Property(x => x.RequestedAlias).HasColumnName("requested_alias").IsRequired();
            request.Property(x => x.TaskType).HasColumnName("task_type");
            request.Property(x => x.PolicyId).HasColumnName("policy_id");
            request.Property(x => x.PolicyVersion).HasColumnName("policy_version");
            request.Property(x => x.SelectedProviderModelId).HasColumnName("selected_provider_model_id");
            request.Property(x => x.RoutingDecision).HasColumnName("routing_decision").HasColumnType("jsonb");
            request.Property(x => x.Streaming).HasColumnName("streaming");
            request.Property(x => x.Status).HasColumnName("status").IsRequired();
            request.Property(x => x.ErrorCode).HasColumnName("error_code");
            request.Property(x => x.AttemptCount).HasColumnName("attempt_count");
            request.Property(x => x.HadFallback).HasColumnName("had_fallback");
            request.Property(x => x.InputBytes).HasColumnName("input_bytes");
            request.Property(x => x.CompletedAt).HasColumnName("completed_at");
            request.Property(x => x.DurationMs).HasColumnName("duration_ms");
            request.Property(x => x.TtftMs).HasColumnName("ttft_ms");
            request.Property(x => x.TotalInputTokens).HasColumnName("total_input_tokens");
            request.Property(x => x.TotalOutputTokens).HasColumnName("total_output_tokens");
            request.Property(x => x.TotalEstimatedCost).HasColumnName("total_estimated_cost").HasColumnType("numeric(20,10)");
            request.Property(x => x.UsageStatus).HasColumnName("usage_status").IsRequired();
            request.Property(x => x.TraceId).HasColumnName("trace_id").IsRequired();

            request.HasIndex(x => new { x.TenantId, x.StartedAt }).IsDescending(false, true).HasDatabaseName("ix_inference_request_tenant_id_started_at");
            request.HasIndex(x => x.StartedAt).IsDescending().HasDatabaseName("ix_inference_request_started_at");
            request.HasIndex(x => new { x.Status, x.StartedAt }).IsDescending(false, true).HasFilter("status <> 'succeeded'").HasDatabaseName("ix_inference_request_status_started_at");
            request.HasIndex(x => new { x.SelectedProviderModelId, x.StartedAt }).IsDescending(false, true).HasDatabaseName("ix_inference_request_selected_provider_model_id_started_at");
            request.HasIndex(x => new { x.RequestedAlias, x.StartedAt }).IsDescending(false, true).HasDatabaseName("ix_inference_request_requested_alias_started_at");
        });

        modelBuilder.Entity<ProviderAttemptRow>(attempt =>
        {
            attempt.ToTable("provider_attempt", t =>
            {
                t.HasCheckConstraint("ck_provider_attempt_kind", "kind in ('primary', 'retry', 'fallback')");
                t.HasCheckConstraint("ck_provider_attempt_status", "status in ('succeeded', 'failed', 'cancelled', 'ambiguous', 'skipped_circuit_open')");
                t.HasCheckConstraint("ck_provider_attempt_error_class", "error_class is null or error_class in ('transient', 'permanent', 'ambiguous')");
            });
            attempt.HasKey(x => new { x.Id, x.StartedAt });
            attempt.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            attempt.Property(x => x.StartedAt).HasColumnName("started_at");
            attempt.Property(x => x.InferenceRequestId).HasColumnName("inference_request_id");
            attempt.Property(x => x.AttemptNumber).HasColumnName("attempt_number");
            attempt.Property(x => x.ProviderId).HasColumnName("provider_id");
            attempt.Property(x => x.ProviderModelId).HasColumnName("provider_model_id");
            attempt.Property(x => x.Kind).HasColumnName("kind").IsRequired();
            attempt.Property(x => x.Status).HasColumnName("status").IsRequired();
            attempt.Property(x => x.ErrorClass).HasColumnName("error_class");
            attempt.Property(x => x.ErrorCode).HasColumnName("error_code");
            attempt.Property(x => x.HttpStatus).HasColumnName("http_status");
            attempt.Property(x => x.ProviderErrorType).HasColumnName("provider_error_type").HasMaxLength(64);
            attempt.Property(x => x.CompletedAt).HasColumnName("completed_at");
            attempt.Property(x => x.DurationMs).HasColumnName("duration_ms");
            attempt.Property(x => x.TtftMs).HasColumnName("ttft_ms");
            attempt.Property(x => x.InputTokens).HasColumnName("input_tokens");
            attempt.Property(x => x.OutputTokens).HasColumnName("output_tokens");
            attempt.Property(x => x.EstimatedCost).HasColumnName("estimated_cost").HasColumnType("numeric(20,10)");

            // A unique index on a partitioned table must contain the partition key. Attempts of one request start in the same
            // month except across a month boundary, which is why the key columns are (request, number, started_at).
            attempt.HasIndex(x => new { x.InferenceRequestId, x.AttemptNumber, x.StartedAt }).IsUnique().HasDatabaseName("ux_provider_attempt_inference_request_id_attempt_number");
            attempt.HasIndex(x => x.InferenceRequestId).HasDatabaseName("ix_provider_attempt_inference_request_id");
        });
    }
}
