using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ModelSwitch.Registry.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "registry");

            migrationBuilder.CreateTable(
                name: "provider",
                schema: "registry",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    adapter_type = table.Column<string>(type: "text", nullable: false),
                    endpoint = table.Column<string>(type: "text", nullable: false),
                    credential_reference = table.Column<string>(type: "text", nullable: true),
                    request_timeout_ms = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider", x => x.id);
                    table.CheckConstraint("ck_provider_adapter_type", "adapter_type in ('openai_compatible', 'ollama')");
                });

            migrationBuilder.CreateTable(
                name: "provider_model",
                schema: "registry",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_model_id = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    capabilities = table.Column<string>(type: "jsonb", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_model", x => x.id);
                    table.ForeignKey(
                        name: "fk_provider_model_provider_id",
                        column: x => x.provider_id,
                        principalSchema: "registry",
                        principalTable: "provider",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "model_price",
                schema: "registry",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    input_per_million = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    cached_input_per_million = table.Column<decimal>(type: "numeric(20,10)", nullable: true),
                    output_per_million = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    currency = table.Column<string>(type: "character(3)", nullable: false),
                    effective_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_admin_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_price", x => x.id);
                    table.CheckConstraint("ck_model_price_currency", "currency = 'USD'");
                    table.CheckConstraint("ck_model_price_non_negative", "input_per_million >= 0 and output_per_million >= 0 and (cached_input_per_million is null or cached_input_per_million >= 0)");
                    table.ForeignKey(
                        name: "fk_model_price_provider_model_id",
                        column: x => x.provider_model_id,
                        principalSchema: "registry",
                        principalTable: "provider_model",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_model_price_provider_model_id_effective_from",
                schema: "registry",
                table: "model_price",
                columns: new[] { "provider_model_id", "effective_from" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_provider_name",
                schema: "registry",
                table: "provider",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_provider_model_provider_id_provider_model_id",
                schema: "registry",
                table: "provider_model",
                columns: new[] { "provider_id", "provider_model_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_price",
                schema: "registry");

            migrationBuilder.DropTable(
                name: "provider_model",
                schema: "registry");

            migrationBuilder.DropTable(
                name: "provider",
                schema: "registry");
        }
    }
}
