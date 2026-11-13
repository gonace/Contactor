using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contactor.Web.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInstallationMethods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccessType",
                table: "Installation",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignInType",
                table: "Installation",
                type: "TEXT",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccessType",
                table: "Installation");

            migrationBuilder.DropColumn(
                name: "SignInType",
                table: "Installation");
        }
    }
}
