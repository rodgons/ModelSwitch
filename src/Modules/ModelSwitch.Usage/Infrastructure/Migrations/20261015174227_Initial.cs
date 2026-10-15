using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ModelSwitch.Usage.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "usage");

            // EF Core cannot create partitioned tables, so both tables are raw SQL. They are range-partitioned by month on
            // started_at (data-model.md, "Partitioning"); a primary key on a partitioned table must include the partition key.
            // The column definitions match the model snapshot exactly (the tests fail on drift).
            migrationBuilder.Sql(
                """
                CREATE TABLE usage.inference_request (
                    id uuid NOT NULL,
                    started_at timestamp with time zone NOT NULL,
                    tenant_id uuid NOT NULL,
                    origin text NOT NULL,
                    api_key_id uuid NULL,
                    evaluation_run_id uuid NULL,
                    client_request_id text NULL,
                    requested_alias text NOT NULL,
                    task_type text NULL,
                    streaming boolean NOT NULL,
                    input_bytes integer NOT NULL,
                    status text NOT NULL,
                    usage_status text NOT NULL,
                    trace_id text NOT NULL,
                    policy_id uuid NULL,
                    policy_version integer NULL,
                    selected_provider_model_id uuid NULL,
                    routing_decision jsonb NULL,
                    error_code text NULL,
                    attempt_count smallint NOT NULL,
                    had_fallback boolean NOT NULL,
                    completed_at timestamp with time zone NULL,
                    duration_ms integer NULL,
                    ttft_ms integer NULL,
                    total_input_tokens integer NULL,
                    total_output_tokens integer NULL,
                    total_estimated_cost numeric(20,10) NULL,
                    CONSTRAINT "PK_inference_request" PRIMARY KEY (id, started_at),
                    CONSTRAINT ck_inference_request_origin CHECK (origin in ('api', 'evaluation')),
                    CONSTRAINT ck_inference_request_origin_ids CHECK ((origin = 'api' and api_key_id is not null and evaluation_run_id is null) or (origin = 'evaluation' and api_key_id is null and evaluation_run_id is not null)),
                    CONSTRAINT ck_inference_request_status CHECK (status in ('running', 'succeeded', 'failed', 'cancelled', 'abandoned')),
                    CONSTRAINT ck_inference_request_usage_status CHECK (usage_status in ('reported', 'estimated', 'partial', 'unavailable'))
                ) PARTITION BY RANGE (started_at);
                """);

            migrationBuilder.Sql(
                """
                CREATE TABLE usage.provider_attempt (
                    id uuid NOT NULL,
                    started_at timestamp with time zone NOT NULL,
                    inference_request_id uuid NOT NULL,
                    attempt_number smallint NOT NULL,
                    provider_id uuid NOT NULL,
                    provider_model_id uuid NOT NULL,
                    kind text NOT NULL,
                    status text NOT NULL,
                    error_class text NULL,
                    error_code text NULL,
                    http_status smallint NULL,
                    provider_error_type character varying(64) NULL,
                    completed_at timestamp with time zone NOT NULL,
                    duration_ms integer NOT NULL,
                    ttft_ms integer NULL,
                    input_tokens integer NULL,
                    output_tokens integer NULL,
                    estimated_cost numeric(20,10) NULL,
                    CONSTRAINT "PK_provider_attempt" PRIMARY KEY (id, started_at),
                    CONSTRAINT ck_provider_attempt_error_class CHECK (error_class is null or error_class in ('transient', 'permanent', 'ambiguous')),
                    CONSTRAINT ck_provider_attempt_kind CHECK (kind in ('primary', 'retry', 'fallback')),
                    CONSTRAINT ck_provider_attempt_status CHECK (status in ('succeeded', 'failed', 'cancelled', 'ambiguous', 'skipped_circuit_open'))
                ) PARTITION BY RANGE (started_at);
                """);

            migrationBuilder.CreateIndex(
                name: "ix_inference_request_requested_alias_started_at",
                schema: "usage",
                table: "inference_request",
                columns: new[] { "requested_alias", "started_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_inference_request_selected_provider_model_id_started_at",
                schema: "usage",
                table: "inference_request",
                columns: new[] { "selected_provider_model_id", "started_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_inference_request_started_at",
                schema: "usage",
                table: "inference_request",
                column: "started_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_inference_request_status_started_at",
                schema: "usage",
                table: "inference_request",
                columns: new[] { "status", "started_at" },
                descending: new[] { false, true },
                filter: "status <> 'succeeded'");

            migrationBuilder.CreateIndex(
                name: "ix_inference_request_tenant_id_started_at",
                schema: "usage",
                table: "inference_request",
                columns: new[] { "tenant_id", "started_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_provider_attempt_inference_request_id",
                schema: "usage",
                table: "provider_attempt",
                column: "inference_request_id");

            migrationBuilder.CreateIndex(
                name: "ux_provider_attempt_inference_request_id_attempt_number",
                schema: "usage",
                table: "provider_attempt",
                columns: new[] { "inference_request_id", "attempt_number", "started_at" },
                unique: true);

            // Partition management. The function is idempotent: it creates the monthly partitions from months_back months
            // before the current UTC month to months_ahead after it, and skips those that exist. The Migrator calls it on
            // every run, so the window keeps moving until the Phase 4 retention job owns it. There is deliberately no DEFAULT
            // partition: it would make CREATE of a later partition fail once it holds rows.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION usage.ensure_request_partitions(months_back integer, months_ahead integer) RETURNS void
                LANGUAGE plpgsql AS $fn$
                DECLARE
                    month_start timestamptz;
                    parent text;
                    m integer;
                BEGIN
                    FOR m IN -months_back .. months_ahead LOOP
                        month_start := (date_trunc('month', now() AT TIME ZONE 'UTC') + make_interval(months => m)) AT TIME ZONE 'UTC';
                        FOREACH parent IN ARRAY ARRAY['inference_request', 'provider_attempt'] LOOP
                            EXECUTE format(
                                'CREATE TABLE IF NOT EXISTS usage.%I PARTITION OF usage.%I FOR VALUES FROM (%L) TO (%L)',
                                parent || '_' || to_char(month_start AT TIME ZONE 'UTC', '"y"YYYY"m"MM'),
                                parent,
                                month_start,
                                month_start + interval '1 month');
                        END LOOP;
                    END LOOP;
                END
                $fn$;
                """);

            migrationBuilder.Sql("SELECT usage.ensure_request_partitions(1, 12);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS usage.ensure_request_partitions(integer, integer);");

            migrationBuilder.DropTable(
                name: "inference_request",
                schema: "usage");

            migrationBuilder.DropTable(
                name: "provider_attempt",
                schema: "usage");
        }
    }
}
