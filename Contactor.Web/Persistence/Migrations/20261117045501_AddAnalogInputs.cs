using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contactor.Web.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalogInputs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnalogInput",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BoardId = table.Column<int>(type: "INTEGER", nullable: false),
                    Channel = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    UnitText = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Volts1 = table.Column<decimal>(type: "TEXT", nullable: true),
                    Value1 = table.Column<decimal>(type: "TEXT", nullable: true),
                    Volts2 = table.Column<decimal>(type: "TEXT", nullable: true),
                    Value2 = table.Column<decimal>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalogInput", x => x.Id);
                    table.CheckConstraint("CK_AnalogInput_Channel", "\"Channel\" BETWEEN 1 AND 64");
                    table.CheckConstraint("CK_AnalogInput_Scale", "(\"Volts1\" IS NULL AND \"Value1\" IS NULL AND \"Volts2\" IS NULL AND \"Value2\" IS NULL) OR (\"Volts1\" IS NOT NULL AND \"Value1\" IS NOT NULL AND \"Volts2\" IS NOT NULL AND \"Value2\" IS NOT NULL)");
                    table.CheckConstraint("CK_AnalogInput_Unit", "\"Unit\" IN ('', 'C', '%', 'bar', 'V', 'other')");
                    table.ForeignKey(
                        name: "FK_AnalogInput_Board_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Board",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnalogInput_BoardId_Channel",
                table: "AnalogInput",
                columns: new[] { "BoardId", "Channel" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnalogInput");
        }
    }
}
