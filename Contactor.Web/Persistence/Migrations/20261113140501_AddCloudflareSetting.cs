using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contactor.Web.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudflareSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CloudflareSetting",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    TunnelToken = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    Address = table.Column<string>(type: "TEXT", maxLength: 253, nullable: true),
                    TeamDomain = table.Column<string>(type: "TEXT", maxLength: 253, nullable: true),
                    Audience = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudflareSetting", x => x.Id);
                    table.CheckConstraint("CK_CloudflareSetting_Id", "\"Id\" = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CloudflareSetting");
        }
    }
}
