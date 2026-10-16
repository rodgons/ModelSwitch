using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ModelSwitch.Usage.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UsageHourlyOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // usage_hourly gains the Request Origin in its key (data-model.md). Every existing row came from API traffic: Evaluation
            // Runs did not exist before this migration, so the rows are backfilled as 'api'. The default exists only for the backfill;
            // the rollup always writes the origin.
            migrationBuilder.DropPrimaryKey(
                name: "PK_usage_hourly",
                schema: "usage",
                table: "usage_hourly");

            migrationBuilder.AddColumn<string>(
                name: "origin",
                schema: "usage",
                table: "usage_hourly",
                type: "text",
                nullable: false,
                defaultValue: "api");

            migrationBuilder.Sql("ALTER TABLE usage.usage_hourly ALTER COLUMN origin DROP DEFAULT;");

            migrationBuilder.AddPrimaryKey(
                name: "PK_usage_hourly",
                schema: "usage",
                table: "usage_hourly",
                columns: new[] { "tenant_id", "provider_model_id", "requested_alias", "origin", "hour" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_usage_hourly_origin",
                schema: "usage",
                table: "usage_hourly",
                sql: "origin in ('api', 'evaluation')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Without origin in the key, an hour's evaluation row and its api row become one: fold the first into the second.
            migrationBuilder.Sql(
                """
                WITH evaluation AS (DELETE FROM usage.usage_hourly WHERE origin = 'evaluation' RETURNING *)
                INSERT INTO usage.usage_hourly AS h (tenant_id, provider_model_id, requested_alias, origin, hour, requests, failed_requests,
                    fallback_requests, attempts, input_tokens, cached_input_tokens, output_tokens, estimated_cost, duration_ms_sum,
                    latency_histogram, ttft_histogram)
                SELECT tenant_id, provider_model_id, requested_alias, 'api', hour, requests, failed_requests, fallback_requests, attempts,
                    input_tokens, cached_input_tokens, output_tokens, estimated_cost, duration_ms_sum, latency_histogram, ttft_histogram
                FROM evaluation
                ON CONFLICT (tenant_id, provider_model_id, requested_alias, origin, hour) DO UPDATE SET
                    requests = h.requests + excluded.requests,
                    failed_requests = h.failed_requests + excluded.failed_requests,
                    fallback_requests = h.fallback_requests + excluded.fallback_requests,
                    attempts = h.attempts + excluded.attempts,
                    input_tokens = h.input_tokens + excluded.input_tokens,
                    cached_input_tokens = h.cached_input_tokens + excluded.cached_input_tokens,
                    output_tokens = h.output_tokens + excluded.output_tokens,
                    estimated_cost = h.estimated_cost + excluded.estimated_cost,
                    duration_ms_sum = h.duration_ms_sum + excluded.duration_ms_sum,
                    latency_histogram = (SELECT array_agg(coalesce(a, 0) + coalesce(b, 0) ORDER BY i)
                                         FROM unnest(h.latency_histogram, excluded.latency_histogram) WITH ORDINALITY AS t(a, b, i)),
                    ttft_histogram = (SELECT array_agg(coalesce(a, 0) + coalesce(b, 0) ORDER BY i)
                                      FROM unnest(h.ttft_histogram, excluded.ttft_histogram) WITH ORDINALITY AS t(a, b, i));
                """);

            migrationBuilder.DropPrimaryKey(
                name: "PK_usage_hourly",
                schema: "usage",
                table: "usage_hourly");

            migrationBuilder.DropCheckConstraint(
                name: "ck_usage_hourly_origin",
                schema: "usage",
                table: "usage_hourly");

            migrationBuilder.DropColumn(
                name: "origin",
                schema: "usage",
                table: "usage_hourly");

            migrationBuilder.AddPrimaryKey(
                name: "PK_usage_hourly",
                schema: "usage",
                table: "usage_hourly",
                columns: new[] { "tenant_id", "provider_model_id", "requested_alias", "hour" });
        }
    }
}
