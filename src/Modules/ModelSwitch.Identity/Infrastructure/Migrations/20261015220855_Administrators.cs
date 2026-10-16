using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ModelSwitch.Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Administrators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "administrator",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    all_tenants = table.Column<bool>(type: "boolean", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    failed_sign_in_count = table.Column<int>(type: "integer", nullable: false),
                    failed_sign_in_window_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_signed_in_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_administrator", x => x.id);
                    table.CheckConstraint("ck_administrator_owner_all_tenants", "role <> 'owner' or all_tenants");
                    table.CheckConstraint("ck_administrator_role", "role in ('owner', 'operator', 'viewer')");
                });

            migrationBuilder.CreateTable(
                name: "audit_record",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    administrator_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_type = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<string>(type: "text", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false),
                    source_ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    trace_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_record", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "data_protection_key",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_protection_key", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "administrator_session",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    administrator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    absolute_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_administrator_session", x => x.id);
                    table.ForeignKey(
                        name: "fk_administrator_session_administrator_id",
                        column: x => x.administrator_id,
                        principalSchema: "identity",
                        principalTable: "administrator",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "administrator_tenant_grant",
                schema: "identity",
                columns: table => new
                {
                    administrator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_administrator_tenant_grant", x => new { x.administrator_id, x.tenant_id });
                    table.ForeignKey(
                        name: "fk_administrator_tenant_grant_administrator_id",
                        column: x => x.administrator_id,
                        principalSchema: "identity",
                        principalTable: "administrator",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_administrator_tenant_grant_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "identity",
                        principalTable: "tenant",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_administrator_email",
                schema: "identity",
                table: "administrator",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_administrator_session_administrator_id",
                schema: "identity",
                table: "administrator_session",
                column: "administrator_id");

            migrationBuilder.CreateIndex(
                name: "ix_administrator_tenant_grant_tenant_id",
                schema: "identity",
                table: "administrator_tenant_grant",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_record_occurred_at",
                schema: "identity",
                table: "audit_record",
                column: "occurred_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_audit_record_tenant_id_occurred_at",
                schema: "identity",
                table: "audit_record",
                columns: new[] { "tenant_id", "occurred_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "administrator_session",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "administrator_tenant_grant",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "audit_record",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "data_protection_key",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "administrator",
                schema: "identity");
        }
    }
}
