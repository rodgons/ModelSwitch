using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ModelSwitch.Usage.Infrastructure.Migrations
{
    /// <summary>
    /// Data only: a <c>budget_period</c> row now exists only for a month in which the Tenant had a Budget (usage-and-budgets.md
    /// section 3). Rows without a limit were created for Tenants without one, and keeping them would keep those Tenants on the
    /// row-locking path; the Usage Records and Reservations they summed are unchanged, so nothing is lost.
    /// </summary>
    public partial class BudgetPeriodOnlyWithBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM usage.budget_period WHERE limit_amount IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to restore: a missing row is created again from the Usage Records when the Tenant next gets a Budget.
        }
    }
}
