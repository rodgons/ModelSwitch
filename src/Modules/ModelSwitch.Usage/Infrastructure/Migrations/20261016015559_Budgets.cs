using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ModelSwitch.Usage.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Budgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "budget",
                schema: "usage",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    limit_amount = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget", x => x.tenant_id);
                    table.CheckConstraint("ck_budget_limit", "limit_amount >= 0");
                });

            migrationBuilder.CreateTable(
                name: "budget_period",
                schema: "usage",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    limit_amount = table.Column<decimal>(type: "numeric(20,10)", nullable: true),
                    reserved_amount = table.Column<decimal>(type: "numeric(20,10)", nullable: false, defaultValue: 0m),
                    spent_amount = table.Column<decimal>(type: "numeric(20,10)", nullable: false, defaultValue: 0m),
                    currency = table.Column<string>(type: "char(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_period", x => new { x.tenant_id, x.period_start });
                    table.CheckConstraint("ck_budget_period_reserved", "reserved_amount >= 0");
                });

            migrationBuilder.CreateTable(
                name: "reservation",
                schema: "usage",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    inference_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reservation", x => x.id);
                    table.CheckConstraint("ck_reservation_status", "status in ('held', 'settled', 'expired')");
                });

            migrationBuilder.CreateTable(
                name: "usage_record",
                schema: "usage",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    inference_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_model_id = table.Column<Guid>(type: "uuid", nullable: true),
                    model_price_id = table.Column<Guid>(type: "uuid", nullable: true),
                    input_tokens = table.Column<int>(type: "integer", nullable: true),
                    cached_input_tokens = table.Column<int>(type: "integer", nullable: true),
                    output_tokens = table.Column<int>(type: "integer", nullable: true),
                    usage_source = table.Column<string>(type: "text", nullable: false),
                    estimate_method = table.Column<string>(type: "text", nullable: true),
                    input_cost = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    output_cost = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    total_cost = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_record", x => x.id);
                    table.CheckConstraint("ck_usage_record_usage_source", "usage_source in ('reported', 'estimated', 'unavailable')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_reservation_status_expires_at",
                schema: "usage",
                table: "reservation",
                columns: new[] { "status", "expires_at" },
                filter: "status = 'held'");

            migrationBuilder.CreateIndex(
                name: "ux_reservation_inference_request_id",
                schema: "usage",
                table: "reservation",
                column: "inference_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_usage_record_inference_request_id",
                schema: "usage",
                table: "usage_record",
                column: "inference_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_usage_record_tenant_id_recorded_at",
                schema: "usage",
                table: "usage_record",
                columns: new[] { "tenant_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "ux_usage_record_provider_attempt_id",
                schema: "usage",
                table: "usage_record",
                column: "provider_attempt_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "budget",
                schema: "usage");

            migrationBuilder.DropTable(
                name: "budget_period",
                schema: "usage");

            migrationBuilder.DropTable(
                name: "reservation",
                schema: "usage");

            migrationBuilder.DropTable(
                name: "usage_record",
                schema: "usage");
        }
    }
}
