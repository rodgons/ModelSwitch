using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ModelSwitch.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.CreateTable(
                name: "tenant",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant", x => x.id);
                    table.CheckConstraint("ck_tenant_status", "status in ('active', 'suspended')");
                });

            migrationBuilder.CreateTable(
                name: "api_key",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    key_prefix = table.Column<string>(type: "character(8)", nullable: false),
                    key_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_admin_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_key", x => x.id);
                    table.CheckConstraint("ck_api_key_key_hash_length", "octet_length(key_hash) = 32");
                    table.ForeignKey(
                        name: "fk_api_key_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "identity",
                        principalTable: "tenant",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_key_tenant_id_created_at",
                schema: "identity",
                table: "api_key",
                columns: new[] { "tenant_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ux_api_key_key_prefix",
                schema: "identity",
                table: "api_key",
                column: "key_prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tenant_name",
                schema: "identity",
                table: "tenant",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_key",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "tenant",
                schema: "identity");
        }
    }
}
