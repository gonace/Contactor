using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contactor.Web.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInstallationLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "Installation",
                type: "TEXT",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Language",
                table: "Installation");
        }
    }
}
