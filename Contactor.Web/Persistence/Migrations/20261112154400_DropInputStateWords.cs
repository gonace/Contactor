using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contactor.Web.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropInputStateWords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClosedText",
                table: "Input");

            migrationBuilder.DropColumn(
                name: "OpenText",
                table: "Input");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClosedText",
                table: "Input",
                type: "TEXT",
                maxLength: 40,
                nullable: false,
                defaultValue: "Closed");

            migrationBuilder.AddColumn<string>(
                name: "OpenText",
                table: "Input",
                type: "TEXT",
                maxLength: 40,
                nullable: false,
                defaultValue: "Open");
        }
    }
}
