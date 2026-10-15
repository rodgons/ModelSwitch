using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ModelSwitch.Registry.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProviderHealthSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_health_snapshot",
                schema: "registry",
                columns: table => new
                {
                    instance_id = table.Column<string>(type: "text", nullable: false),
                    provider_model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    circuit_state = table.Column<string>(type: "text", nullable: false),
                    window_failure_rate = table.Column<float>(type: "real", nullable: false),
                    window_calls = table.Column<int>(type: "integer", nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_health_snapshot", x => new { x.instance_id, x.provider_model_id });
                    table.CheckConstraint("ck_provider_health_snapshot_circuit_state", "circuit_state in ('closed', 'half_open', 'open')");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_health_snapshot",
                schema: "registry");
        }
    }
}
