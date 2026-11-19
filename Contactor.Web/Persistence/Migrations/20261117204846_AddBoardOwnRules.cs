using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contactor.Web.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBoardOwnRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BoardOwnRule",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BoardId = table.Column<int>(type: "INTEGER", nullable: false),
                    Channel = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    FoundUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    KeptUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    KeptBy = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardOwnRule", x => x.Id);
                    table.CheckConstraint("CK_BoardOwnRule_Channel", "\"Channel\" BETWEEN 1 AND 64");
                    table.CheckConstraint("CK_BoardOwnRule_Kind", "\"Kind\" IN ('timer', 'input-linkage', 'input-toggle', 'input-edge')");
                    table.ForeignKey(
                        name: "FK_BoardOwnRule_Board_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Board",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BoardOwnRule_BoardId_Channel_Kind",
                table: "BoardOwnRule",
                columns: new[] { "BoardId", "Channel", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BoardOwnRule");
        }
    }
}
