using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ModelSwitch.Registry.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProviderSecret : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_secret",
                schema: "registry",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    tag = table.Column<byte[]>(type: "bytea", nullable: false),
                    key_version = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_secret", x => x.id);
                    table.CheckConstraint("ck_provider_secret_nonce_length", "octet_length(nonce) = 12");
                    table.CheckConstraint("ck_provider_secret_tag_length", "octet_length(tag) = 16");
                    table.ForeignKey(
                        name: "fk_provider_secret_provider_id",
                        column: x => x.provider_id,
                        principalSchema: "registry",
                        principalTable: "provider",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_provider_secret_provider_id",
                schema: "registry",
                table: "provider_secret",
                column: "provider_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_secret",
                schema: "registry");
        }
    }
}
