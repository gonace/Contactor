using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contactor.Web.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInputPerBoardInput : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Input_Id",
                table: "Input");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Input",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "InputId",
                table: "EventLog",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Input_Id",
                table: "Input",
                sql: "\"Id\" BETWEEN 1 AND 64");

            migrationBuilder.CreateIndex(
                name: "IX_EventLog_InputId_TimestampUtc",
                table: "EventLog",
                columns: new[] { "InputId", "TimestampUtc" });

            // Until now only the board's first input was logged, and its row was named "Input". EF Core can't express
            // an update in a migration, hence plain SQL.
            migrationBuilder.Sql("UPDATE \"EventLog\" SET \"InputId\" = 1 WHERE \"Action\" = 'input-changed';");
            migrationBuilder.Sql("UPDATE \"Input\" SET \"Name\" = 'Input 1' WHERE \"Id\" = 1 AND \"Name\" = 'Input';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The one-row check constraint below fails on any other input, so those go first.
            migrationBuilder.Sql("DELETE FROM \"Input\" WHERE \"Id\" > 1;");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Input_Id",
                table: "Input");

            migrationBuilder.DropIndex(
                name: "IX_EventLog_InputId_TimestampUtc",
                table: "EventLog");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Input");

            migrationBuilder.DropColumn(
                name: "InputId",
                table: "EventLog");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Input_Id",
                table: "Input",
                sql: "\"Id\" = 1");
        }
    }
}
