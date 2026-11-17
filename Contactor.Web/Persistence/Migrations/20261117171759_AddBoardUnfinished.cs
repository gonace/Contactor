using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contactor.Web.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBoardUnfinished : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsUnfinished",
                table: "Board",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsUnfinished",
                table: "Board");
        }
    }
}
