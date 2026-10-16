using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ModelSwitch.Usage.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InferenceRequestEvaluationRunIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // IEvaluationSpend sums a run's requests by evaluation_run_id (data-model.md "usage"). Created on the partitioned parent,
            // PostgreSQL builds it on every existing partition and on each one ensure_request_partitions creates later. Partial,
            // because API traffic (the bulk of the rows) never has a run. CONCURRENTLY is not supported on a partitioned parent.
            migrationBuilder.CreateIndex(
                name: "ix_inference_request_evaluation_run_id",
                schema: "usage",
                table: "inference_request",
                column: "evaluation_run_id",
                filter: "evaluation_run_id is not null");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_inference_request_evaluation_run_id",
                schema: "usage",
                table: "inference_request");
        }
    }
}
