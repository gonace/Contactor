using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contactor.Web.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "RelayId",
                table: "Schedule",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<int>(
                name: "GroupId",
                table: "Schedule",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Schedule_GroupId",
                table: "Schedule",
                column: "GroupId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Schedule_Target",
                table: "Schedule",
                sql: "(\"RelayId\" IS NULL) <> (\"GroupId\" IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_Schedule_RelayGroup_GroupId",
                table: "Schedule",
                column: "GroupId",
                principalTable: "RelayGroup",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Schedule_RelayGroup_GroupId",
                table: "Schedule");

            migrationBuilder.DropIndex(
                name: "IX_Schedule_GroupId",
                table: "Schedule");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Schedule_Target",
                table: "Schedule");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Schedule");

            migrationBuilder.AlterColumn<int>(
                name: "RelayId",
                table: "Schedule",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
