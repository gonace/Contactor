using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contactor.Web.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnRuleKindAutoOn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_BoardOwnRule_Kind",
                table: "BoardOwnRule");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BoardOwnRule_Kind",
                table: "BoardOwnRule",
                sql: "\"Kind\" IN ('timer', 'input-linkage', 'input-toggle', 'input-edge', 'auto-on')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The older check refuses the rows of the new kind: they go first. The board reports them again after an
            // upgrade, as found.
            migrationBuilder.DeleteData(
                table: "BoardOwnRule",
                keyColumn: "Kind",
                keyValue: "auto-on");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BoardOwnRule_Kind",
                table: "BoardOwnRule");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BoardOwnRule_Kind",
                table: "BoardOwnRule",
                sql: "\"Kind\" IN ('timer', 'input-linkage', 'input-toggle', 'input-edge')");
        }
    }
}
