using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ModelSwitch.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TenantLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_limits",
                schema: "identity",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requests_per_minute = table.Column<int>(type: "integer", nullable: true),
                    max_concurrent = table.Column<int>(type: "integer", nullable: true),
                    max_request_cost = table.Column<decimal>(type: "numeric(20,10)", nullable: true),
                    budget_amount = table.Column<decimal>(type: "numeric(20,10)", nullable: true),
                    budget_period = table.Column<string>(type: "text", nullable: true),
                    allowed_aliases = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{*}'"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_limits", x => x.tenant_id);
                    table.CheckConstraint("ck_tenant_limits_budget_period", "budget_period is null or budget_period = 'month'");
                    table.CheckConstraint("ck_tenant_limits_max_concurrent", "max_concurrent is null or max_concurrent > 0");
                    table.CheckConstraint("ck_tenant_limits_requests_per_minute", "requests_per_minute is null or requests_per_minute > 0");
                    table.ForeignKey(
                        name: "fk_tenant_limits_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "identity",
                        principalTable: "tenant",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_limits",
                schema: "identity");
        }
    }
}
