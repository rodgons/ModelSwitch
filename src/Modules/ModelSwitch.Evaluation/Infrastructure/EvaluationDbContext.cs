using Microsoft.EntityFrameworkCore;
using ModelSwitch.Evaluation.Domain.Datasets;
using ModelSwitch.Evaluation.Domain.Prompts;
using ModelSwitch.Evaluation.Domain.Runs;

namespace ModelSwitch.Evaluation.Infrastructure;

/// <summary>
/// The <c>evaluation</c> schema (data-model.md, "evaluation"). Every Tenant-owned root (Dataset, Prompt, Comparison, Evaluation Run)
/// carries <c>tenant_id</c>; queries filter it by the caller's Tenant Grant in the application services. Evaluation data lives
/// until an Administrator deletes it, and a delete cascades down the foreign keys (data-model.md, "Retention").
/// </summary>
internal sealed class EvaluationDbContext(DbContextOptions<EvaluationDbContext> options) : DbContext(options)
{
    public const string Schema = "evaluation";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<Dataset> Datasets => Set<Dataset>();

    public DbSet<DatasetVersion> DatasetVersions => Set<DatasetVersion>();

    public DbSet<Case> Cases => Set<Case>();

    public DbSet<Prompt> Prompts => Set<Prompt>();

    public DbSet<PromptVersion> PromptVersions => Set<PromptVersion>();

    public DbSet<Comparison> Comparisons => Set<Comparison>();

    public DbSet<EvaluationRun> Runs => Set<EvaluationRun>();

    public DbSet<CaseResult> CaseResults => Set<CaseResult>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        ConfigureDatasets(modelBuilder);
        ConfigurePrompts(modelBuilder);
        ConfigureRuns(modelBuilder);
    }

    private static string InList(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));

    private static void ConfigureDatasets(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Dataset>(dataset =>
        {
            dataset.ToTable("dataset", t =>
            {
                t.HasCheckConstraint("ck_dataset_task_kind", $"task_kind in ({InList(TaskKinds.All)})");
                t.HasCheckConstraint("ck_dataset_latest_version", "latest_version >= 0");
            });
            dataset.HasKey(x => x.Id);
            dataset.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            dataset.Property(x => x.TenantId).HasColumnName("tenant_id");
            dataset.Property(x => x.Name).HasColumnName("name").IsRequired();
            dataset.Property(x => x.TaskKind).HasColumnName("task_kind").IsRequired();
            dataset.Property(x => x.LatestVersion).HasColumnName("latest_version");
            dataset.Property(x => x.CreatedAt).HasColumnName("created_at");
            dataset.HasIndex(x => new { x.TenantId, x.Name }).IsUnique().HasDatabaseName("ux_dataset_tenant_id_name");

            // Lists are newest first by id (UUIDv7), within a Tenant or across the grant.
            dataset.HasIndex(x => new { x.TenantId, x.Id }).HasDatabaseName("ix_dataset_tenant_id_id");
        });

        modelBuilder.Entity<DatasetVersion>(version =>
        {
            version.ToTable("dataset_version", t =>
            {
                t.HasCheckConstraint("ck_dataset_version_version", "version >= 1");
                t.HasCheckConstraint("ck_dataset_version_case_count", "case_count >= 1");
                t.HasCheckConstraint("ck_dataset_version_content_sha256", "content_sha256 ~ '^[0-9a-f]{64}$'");
            });
            version.HasKey(x => new { x.DatasetId, x.Version });
            version.Property(x => x.DatasetId).HasColumnName("dataset_id");
            version.Property(x => x.Version).HasColumnName("version");
            version.Property(x => x.CaseCount).HasColumnName("case_count");
            version.Property(x => x.ContentSha256).HasColumnName("content_sha256").IsRequired();
            version.Property(x => x.ScoringSchemaJson).HasColumnName("scoring_schema").HasColumnType("jsonb").IsRequired();
            version.Property(x => x.CreatedAt).HasColumnName("created_at");
            version.Property(x => x.CreatedByAdminId).HasColumnName("created_by_admin_id");
            version.HasOne<Dataset>().WithMany().HasForeignKey(x => x.DatasetId)
                .HasConstraintName("fk_dataset_version_dataset_id").OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Case>(@case =>
        {
            @case.ToTable("eval_case", t => t.HasCheckConstraint("ck_eval_case_ordinal", "ordinal >= 0"));
            @case.HasKey(x => x.Id);
            @case.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            @case.Property(x => x.DatasetId).HasColumnName("dataset_id");
            @case.Property(x => x.DatasetVersion).HasColumnName("dataset_version");
            @case.Property(x => x.ExternalId).HasColumnName("external_id").IsRequired();
            @case.Property(x => x.InputJson).HasColumnName("input").HasColumnType("jsonb").IsRequired();
            @case.Property(x => x.ExpectedJson).HasColumnName("expected").HasColumnType("jsonb").IsRequired();
            @case.Property(x => x.MetadataJson).HasColumnName("metadata").HasColumnType("jsonb");
            @case.Property(x => x.Ordinal).HasColumnName("ordinal");
            @case.HasOne<DatasetVersion>().WithMany().HasForeignKey(x => new { x.DatasetId, x.DatasetVersion })
                .HasConstraintName("fk_eval_case_dataset_version").OnDelete(DeleteBehavior.Cascade);
            @case.HasIndex(x => new { x.DatasetId, x.DatasetVersion, x.ExternalId }).IsUnique().HasDatabaseName("ux_eval_case_dataset_version_external_id");

            // Cases are listed and run in upload order.
            @case.HasIndex(x => new { x.DatasetId, x.DatasetVersion, x.Ordinal }).IsUnique().HasDatabaseName("ux_eval_case_dataset_version_ordinal");
        });
    }

    private static void ConfigurePrompts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Prompt>(prompt =>
        {
            prompt.ToTable("prompt", t => t.HasCheckConstraint("ck_prompt_latest_version", "latest_version >= 0"));
            prompt.HasKey(x => x.Id);
            prompt.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            prompt.Property(x => x.TenantId).HasColumnName("tenant_id");
            prompt.Property(x => x.Name).HasColumnName("name").IsRequired();
            prompt.Property(x => x.LatestVersion).HasColumnName("latest_version");
            prompt.Property(x => x.CreatedAt).HasColumnName("created_at");
            prompt.HasIndex(x => new { x.TenantId, x.Name }).IsUnique().HasDatabaseName("ux_prompt_tenant_id_name");
            prompt.HasIndex(x => new { x.TenantId, x.Id }).HasDatabaseName("ix_prompt_tenant_id_id");
        });

        modelBuilder.Entity<PromptVersion>(version =>
        {
            version.ToTable("prompt_version", t => t.HasCheckConstraint("ck_prompt_version_version", "version >= 1"));
            version.HasKey(x => new { x.PromptId, x.Version });
            version.Property(x => x.PromptId).HasColumnName("prompt_id");
            version.Property(x => x.Version).HasColumnName("version");
            version.Property(x => x.TemplateJson).HasColumnName("template").HasColumnType("jsonb").IsRequired();
            version.Property(x => x.CreatedAt).HasColumnName("created_at");
            version.Property(x => x.CreatedByAdminId).HasColumnName("created_by_admin_id");
            version.HasOne<Prompt>().WithMany().HasForeignKey(x => x.PromptId)
                .HasConstraintName("fk_prompt_version_prompt_id").OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureRuns(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Comparison>(comparison =>
        {
            comparison.ToTable("comparison");
            comparison.HasKey(x => x.Id);
            comparison.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            comparison.Property(x => x.TenantId).HasColumnName("tenant_id");
            comparison.Property(x => x.DatasetId).HasColumnName("dataset_id");
            comparison.Property(x => x.DatasetVersion).HasColumnName("dataset_version");
            comparison.Property(x => x.Name).HasColumnName("name").IsRequired();
            comparison.Property(x => x.CreatedAt).HasColumnName("created_at");
            comparison.Property(x => x.CreatedByAdminId).HasColumnName("created_by_admin_id");
            comparison.HasOne<DatasetVersion>().WithMany().HasForeignKey(x => new { x.DatasetId, x.DatasetVersion })
                .HasConstraintName("fk_comparison_dataset_version").OnDelete(DeleteBehavior.Cascade);
            comparison.HasIndex(x => new { x.TenantId, x.Id }).HasDatabaseName("ix_comparison_tenant_id_id");
        });

        modelBuilder.Entity<EvaluationRun>(run =>
        {
            run.ToTable("evaluation_run", t =>
            {
                t.HasCheckConstraint("ck_evaluation_run_status", $"status in ({InList(EvaluationRunStatuses.All)})");
                t.HasCheckConstraint("ck_evaluation_run_config_sha256", "config_sha256 ~ '^[0-9a-f]{64}$'");
                t.HasCheckConstraint("ck_evaluation_run_max_estimated_cost", "max_estimated_cost is null or max_estimated_cost >= 0");
                t.HasCheckConstraint("ck_evaluation_run_cases", "cases_total >= 0 and cases_done >= 0 and cases_failed >= 0 and cases_done <= cases_total and cases_failed <= cases_done");
            });
            run.HasKey(x => x.Id);
            run.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            run.Property(x => x.ComparisonId).HasColumnName("comparison_id");
            run.Property(x => x.TenantId).HasColumnName("tenant_id");
            run.Property(x => x.DatasetId).HasColumnName("dataset_id");
            run.Property(x => x.DatasetVersion).HasColumnName("dataset_version");
            run.Property(x => x.RunConfigurationJson).HasColumnName("run_configuration").HasColumnType("jsonb").IsRequired();
            run.Property(x => x.ConfigSha256).HasColumnName("config_sha256").IsRequired();
            run.Property(x => x.MaxEstimatedCost).HasColumnName("max_estimated_cost").HasColumnType("numeric(20,10)");
            run.Property(x => x.Status).HasColumnName("status").IsRequired();
            run.Property(x => x.CasesTotal).HasColumnName("cases_total");
            run.Property(x => x.CasesDone).HasColumnName("cases_done");
            run.Property(x => x.CasesFailed).HasColumnName("cases_failed");
            run.Property(x => x.AggregatesJson).HasColumnName("aggregates").HasColumnType("jsonb");
            run.Property(x => x.CreatedAt).HasColumnName("created_at");
            run.Property(x => x.StartedAt).HasColumnName("started_at");
            run.Property(x => x.CompletedAt).HasColumnName("completed_at");
            run.Property(x => x.Error).HasColumnName("error");
            run.HasOne<Comparison>().WithMany().HasForeignKey(x => x.ComparisonId)
                .HasConstraintName("fk_evaluation_run_comparison_id").OnDelete(DeleteBehavior.Cascade);
            run.HasOne<DatasetVersion>().WithMany().HasForeignKey(x => new { x.DatasetId, x.DatasetVersion })
                .HasConstraintName("fk_evaluation_run_dataset_version").OnDelete(DeleteBehavior.Cascade);
            run.HasIndex(x => x.ComparisonId).HasDatabaseName("ix_evaluation_run_comparison_id");
            run.HasIndex(x => new { x.TenantId, x.Id }).HasDatabaseName("ix_evaluation_run_tenant_id_id");
            run.HasIndex(x => new { x.DatasetId, x.DatasetVersion }).HasDatabaseName("ix_evaluation_run_dataset_version");
        });

        modelBuilder.Entity<CaseResult>(result =>
        {
            result.ToTable("case_result", t =>
            {
                t.HasCheckConstraint("ck_case_result_status", $"status in ({InList(CaseResultStatuses.All)})");
                t.HasCheckConstraint("ck_case_result_score", "score is null or (score >= 0 and score <= 1)");
                t.HasCheckConstraint("ck_case_result_unscored", "status not in ('errored', 'skipped') or score is null");
            });
            result.HasKey(x => x.Id);
            result.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            result.Property(x => x.RunId).HasColumnName("run_id");
            result.Property(x => x.CaseId).HasColumnName("case_id");
            result.Property(x => x.Status).HasColumnName("status").IsRequired();
            result.Property(x => x.Score).HasColumnName("score").HasColumnType("numeric(6,5)");
            result.Property(x => x.PredictedJson).HasColumnName("predicted").HasColumnType("jsonb");
            result.Property(x => x.OutputText).HasColumnName("output_text");
            result.Property(x => x.ScoreDetailsJson).HasColumnName("score_details").HasColumnType("jsonb").IsRequired();
            result.Property(x => x.InferenceRequestId).HasColumnName("inference_request_id");
            result.Property(x => x.InputTokens).HasColumnName("input_tokens");
            result.Property(x => x.OutputTokens).HasColumnName("output_tokens");
            result.Property(x => x.EstimatedCost).HasColumnName("estimated_cost").HasColumnType("numeric(20,10)");
            result.Property(x => x.JudgeInferenceRequestId).HasColumnName("judge_inference_request_id");
            result.Property(x => x.JudgeInputTokens).HasColumnName("judge_input_tokens");
            result.Property(x => x.JudgeOutputTokens).HasColumnName("judge_output_tokens");
            result.Property(x => x.JudgeEstimatedCost).HasColumnName("judge_estimated_cost").HasColumnType("numeric(20,10)");
            result.Property(x => x.LatencyMs).HasColumnName("latency_ms");
            result.Property(x => x.ErrorCode).HasColumnName("error_code");
            result.Property(x => x.CreatedAt).HasColumnName("created_at");
            result.HasOne<EvaluationRun>().WithMany().HasForeignKey(x => x.RunId)
                .HasConstraintName("fk_case_result_run_id").OnDelete(DeleteBehavior.Cascade);
            result.HasOne<Case>().WithMany().HasForeignKey(x => x.CaseId)
                .HasConstraintName("fk_case_result_case_id").OnDelete(DeleteBehavior.Cascade);

            // Resuming is idempotent: a Case already recorded for the run is never recorded again (NFR-02).
            result.HasIndex(x => new { x.RunId, x.CaseId }).IsUnique().HasDatabaseName("ux_case_result_run_id_case_id");
            result.HasIndex(x => x.CaseId).HasDatabaseName("ix_case_result_case_id");

            // The request detail page follows a request back to "judge for Case X of run Y".
            result.HasIndex(x => x.InferenceRequestId).HasDatabaseName("ix_case_result_inference_request_id");
            result.HasIndex(x => x.JudgeInferenceRequestId).HasFilter("judge_inference_request_id is not null").HasDatabaseName("ix_case_result_judge_inference_request_id");
        });
    }
}
