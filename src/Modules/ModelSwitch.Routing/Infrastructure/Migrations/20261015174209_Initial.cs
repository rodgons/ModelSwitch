using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ModelSwitch.Routing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "routing");

            migrationBuilder.CreateTable(
                name: "routing_policy",
                schema: "routing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    latest_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_routing_policy", x => x.id);
                    table.CheckConstraint("ck_routing_policy_latest_version", "latest_version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "model_alias",
                schema: "routing",
                columns: table => new
                {
                    alias = table.Column<string>(type: "text", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_alias", x => x.alias);
                    table.CheckConstraint("ck_model_alias_alias", "alias ~ '^[a-z0-9][a-z0-9._-]{0,63}$'");
                    table.ForeignKey(
                        name: "fk_model_alias_policy_id",
                        column: x => x.policy_id,
                        principalSchema: "routing",
                        principalTable: "routing_policy",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "routing_policy_version",
                schema: "routing",
                columns: table => new
                {
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    definition = table.Column<string>(type: "jsonb", nullable: false),
                    resolved_candidates = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_admin_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_routing_policy_version", x => new { x.policy_id, x.version });
                    table.CheckConstraint("ck_routing_policy_version_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_routing_policy_version_policy_id",
                        column: x => x.policy_id,
                        principalSchema: "routing",
                        principalTable: "routing_policy",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_model_alias_policy_id",
                schema: "routing",
                table: "model_alias",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ux_routing_policy_name",
                schema: "routing",
                table: "routing_policy",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_alias",
                schema: "routing");

            migrationBuilder.DropTable(
                name: "routing_policy_version",
                schema: "routing");

            migrationBuilder.DropTable(
                name: "routing_policy",
                schema: "routing");
        }
    }
}
