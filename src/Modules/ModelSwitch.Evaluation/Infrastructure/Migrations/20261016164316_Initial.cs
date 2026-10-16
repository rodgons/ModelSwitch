using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ModelSwitch.Evaluation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "evaluation");

            migrationBuilder.CreateTable(
                name: "dataset",
                schema: "evaluation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    task_kind = table.Column<string>(type: "text", nullable: false),
                    latest_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dataset", x => x.id);
                    table.CheckConstraint("ck_dataset_latest_version", "latest_version >= 0");
                    table.CheckConstraint("ck_dataset_task_kind", "task_kind in ('extraction', 'classification', 'free_text')");
                });

            migrationBuilder.CreateTable(
                name: "prompt",
                schema: "evaluation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    latest_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prompt", x => x.id);
                    table.CheckConstraint("ck_prompt_latest_version", "latest_version >= 0");
                });

            migrationBuilder.CreateTable(
                name: "dataset_version",
                schema: "evaluation",
                columns: table => new
                {
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    case_count = table.Column<int>(type: "integer", nullable: false),
                    content_sha256 = table.Column<string>(type: "text", nullable: false),
                    scoring_schema = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_admin_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dataset_version", x => new { x.dataset_id, x.version });
                    table.CheckConstraint("ck_dataset_version_case_count", "case_count >= 1");
                    table.CheckConstraint("ck_dataset_version_content_sha256", "content_sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_dataset_version_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_dataset_version_dataset_id",
                        column: x => x.dataset_id,
                        principalSchema: "evaluation",
                        principalTable: "dataset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "prompt_version",
                schema: "evaluation",
                columns: table => new
                {
                    prompt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    template = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_admin_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prompt_version", x => new { x.prompt_id, x.version });
                    table.CheckConstraint("ck_prompt_version_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_prompt_version_prompt_id",
                        column: x => x.prompt_id,
                        principalSchema: "evaluation",
                        principalTable: "prompt",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "comparison",
                schema: "evaluation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dataset_version = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_admin_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comparison", x => x.id);
                    table.ForeignKey(
                        name: "fk_comparison_dataset_version",
                        columns: x => new { x.dataset_id, x.dataset_version },
                        principalSchema: "evaluation",
                        principalTable: "dataset_version",
                        principalColumns: new[] { "dataset_id", "version" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "eval_case",
                schema: "evaluation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dataset_version = table.Column<int>(type: "integer", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: false),
                    input = table.Column<string>(type: "jsonb", nullable: false),
                    expected = table.Column<string>(type: "jsonb", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    ordinal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eval_case", x => x.id);
                    table.CheckConstraint("ck_eval_case_ordinal", "ordinal >= 0");
                    table.ForeignKey(
                        name: "fk_eval_case_dataset_version",
                        columns: x => new { x.dataset_id, x.dataset_version },
                        principalSchema: "evaluation",
                        principalTable: "dataset_version",
                        principalColumns: new[] { "dataset_id", "version" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "evaluation_run",
                schema: "evaluation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    comparison_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dataset_version = table.Column<int>(type: "integer", nullable: false),
                    run_configuration = table.Column<string>(type: "jsonb", nullable: false),
                    config_sha256 = table.Column<string>(type: "text", nullable: false),
                    max_estimated_cost = table.Column<decimal>(type: "numeric(20,10)", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    cases_total = table.Column<int>(type: "integer", nullable: false),
                    cases_done = table.Column<int>(type: "integer", nullable: false),
                    cases_failed = table.Column<int>(type: "integer", nullable: false),
                    aggregates = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evaluation_run", x => x.id);
                    table.CheckConstraint("ck_evaluation_run_cases", "cases_total >= 0 and cases_done >= 0 and cases_failed >= 0 and cases_done <= cases_total and cases_failed <= cases_done");
                    table.CheckConstraint("ck_evaluation_run_config_sha256", "config_sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_evaluation_run_max_estimated_cost", "max_estimated_cost is null or max_estimated_cost >= 0");
                    table.CheckConstraint("ck_evaluation_run_status", "status in ('queued', 'running', 'succeeded', 'failed', 'cancelled')");
                    table.ForeignKey(
                        name: "fk_evaluation_run_comparison_id",
                        column: x => x.comparison_id,
                        principalSchema: "evaluation",
                        principalTable: "comparison",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_evaluation_run_dataset_version",
                        columns: x => new { x.dataset_id, x.dataset_version },
                        principalSchema: "evaluation",
                        principalTable: "dataset_version",
                        principalColumns: new[] { "dataset_id", "version" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "case_result",
                schema: "evaluation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    score = table.Column<decimal>(type: "numeric(6,5)", nullable: true),
                    predicted = table.Column<string>(type: "jsonb", nullable: true),
                    output_text = table.Column<string>(type: "text", nullable: true),
                    score_details = table.Column<string>(type: "jsonb", nullable: false),
                    inference_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    input_tokens = table.Column<int>(type: "integer", nullable: true),
                    output_tokens = table.Column<int>(type: "integer", nullable: true),
                    estimated_cost = table.Column<decimal>(type: "numeric(20,10)", nullable: true),
                    judge_inference_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    judge_input_tokens = table.Column<int>(type: "integer", nullable: true),
                    judge_output_tokens = table.Column<int>(type: "integer", nullable: true),
                    judge_estimated_cost = table.Column<decimal>(type: "numeric(20,10)", nullable: true),
                    latency_ms = table.Column<int>(type: "integer", nullable: true),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_case_result", x => x.id);
                    table.CheckConstraint("ck_case_result_score", "score is null or (score >= 0 and score <= 1)");
                    table.CheckConstraint("ck_case_result_status", "status in ('passed', 'failed', 'errored', 'skipped')");
                    table.CheckConstraint("ck_case_result_unscored", "status not in ('errored', 'skipped') or score is null");
                    table.ForeignKey(
                        name: "fk_case_result_case_id",
                        column: x => x.case_id,
                        principalSchema: "evaluation",
                        principalTable: "eval_case",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_case_result_run_id",
                        column: x => x.run_id,
                        principalSchema: "evaluation",
                        principalTable: "evaluation_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_case_result_case_id",
                schema: "evaluation",
                table: "case_result",
                column: "case_id");

            migrationBuilder.CreateIndex(
                name: "ix_case_result_inference_request_id",
                schema: "evaluation",
                table: "case_result",
                column: "inference_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_case_result_judge_inference_request_id",
                schema: "evaluation",
                table: "case_result",
                column: "judge_inference_request_id",
                filter: "judge_inference_request_id is not null");

            migrationBuilder.CreateIndex(
                name: "ux_case_result_run_id_case_id",
                schema: "evaluation",
                table: "case_result",
                columns: new[] { "run_id", "case_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_comparison_dataset_id_dataset_version",
                schema: "evaluation",
                table: "comparison",
                columns: new[] { "dataset_id", "dataset_version" });

            migrationBuilder.CreateIndex(
                name: "ix_comparison_tenant_id_id",
                schema: "evaluation",
                table: "comparison",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_dataset_tenant_id_id",
                schema: "evaluation",
                table: "dataset",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_dataset_tenant_id_name",
                schema: "evaluation",
                table: "dataset",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_eval_case_dataset_version_external_id",
                schema: "evaluation",
                table: "eval_case",
                columns: new[] { "dataset_id", "dataset_version", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_eval_case_dataset_version_ordinal",
                schema: "evaluation",
                table: "eval_case",
                columns: new[] { "dataset_id", "dataset_version", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_evaluation_run_comparison_id",
                schema: "evaluation",
                table: "evaluation_run",
                column: "comparison_id");

            migrationBuilder.CreateIndex(
                name: "ix_evaluation_run_dataset_version",
                schema: "evaluation",
                table: "evaluation_run",
                columns: new[] { "dataset_id", "dataset_version" });

            migrationBuilder.CreateIndex(
                name: "ix_evaluation_run_tenant_id_id",
                schema: "evaluation",
                table: "evaluation_run",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_prompt_tenant_id_id",
                schema: "evaluation",
                table: "prompt",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_prompt_tenant_id_name",
                schema: "evaluation",
                table: "prompt",
                columns: new[] { "tenant_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "case_result",
                schema: "evaluation");

            migrationBuilder.DropTable(
                name: "prompt_version",
                schema: "evaluation");

            migrationBuilder.DropTable(
                name: "eval_case",
                schema: "evaluation");

            migrationBuilder.DropTable(
                name: "evaluation_run",
                schema: "evaluation");

            migrationBuilder.DropTable(
                name: "prompt",
                schema: "evaluation");

            migrationBuilder.DropTable(
                name: "comparison",
                schema: "evaluation");

            migrationBuilder.DropTable(
                name: "dataset_version",
                schema: "evaluation");

            migrationBuilder.DropTable(
                name: "dataset",
                schema: "evaluation");
        }
    }
}
