using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ModelSwitch.Usage.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UsageHourly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "usage_hourly",
                schema: "usage",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_alias = table.Column<string>(type: "text", nullable: false),
                    hour = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requests = table.Column<int>(type: "integer", nullable: false),
                    failed_requests = table.Column<int>(type: "integer", nullable: false),
                    fallback_requests = table.Column<int>(type: "integer", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cached_input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    estimated_cost = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    duration_ms_sum = table.Column<long>(type: "bigint", nullable: false),
                    latency_histogram = table.Column<int[]>(type: "integer[]", nullable: false),
                    ttft_histogram = table.Column<int[]>(type: "integer[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_hourly", x => new { x.tenant_id, x.provider_model_id, x.requested_alias, x.hour });
                });

            migrationBuilder.CreateIndex(
                name: "ix_usage_hourly_hour",
                schema: "usage",
                table: "usage_hourly",
                column: "hour");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "usage_hourly",
                schema: "usage");
        }
    }
}
