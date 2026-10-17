using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ModelSwitch.Usage.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReservationRetentionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_reservation_settled_at",
                schema: "usage",
                table: "reservation",
                column: "settled_at",
                filter: "status <> 'held'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_reservation_settled_at",
                schema: "usage",
                table: "reservation");
        }
    }
}
