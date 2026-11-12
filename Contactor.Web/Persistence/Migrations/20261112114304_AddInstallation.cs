using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contactor.Web.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInstallation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Installation",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    BoardType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    SetupDoneUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Installation", x => x.Id);
                    table.CheckConstraint("CK_Installation_Id", "\"Id\" = 1");
                });

            // An installation already in use (someone has signed in, saved the board's address or switched
            // something) has been set up: it must never land on the setup guide after this upgrade. A new, empty
            // database gets no row, and the app opens on the guide. EF Core can't express this in a migration,
            // hence plain SQL; the time is in the format EF Core writes.
            migrationBuilder.Sql(
                "INSERT INTO \"Installation\" (\"Id\", \"BoardType\", \"SetupDoneUtc\") " +
                "SELECT 1, NULL, strftime('%Y-%m-%d %H:%M:%S', 'now') " +
                "WHERE EXISTS (SELECT 1 FROM \"User\") OR EXISTS (SELECT 1 FROM \"BoardSetting\") OR EXISTS (SELECT 1 FROM \"EventLog\");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Installation");
        }
    }
}
