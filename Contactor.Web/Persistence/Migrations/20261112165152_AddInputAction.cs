using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contactor.Web.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInputAction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InputAction",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InputId = table.Column<int>(type: "INTEGER", nullable: false),
                    RelayId = table.Column<int>(type: "INTEGER", nullable: true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: true),
                    WhenClosed = table.Column<bool>(type: "INTEGER", nullable: true),
                    WhenOpened = table.Column<bool>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InputAction", x => x.Id);
                    table.CheckConstraint("CK_InputAction_Target", "(\"RelayId\" IS NULL) <> (\"GroupId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_InputAction_Input_InputId",
                        column: x => x.InputId,
                        principalTable: "Input",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InputAction_RelayGroup_GroupId",
                        column: x => x.GroupId,
                        principalTable: "RelayGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InputAction_Relay_RelayId",
                        column: x => x.RelayId,
                        principalTable: "Relay",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InputAction_GroupId",
                table: "InputAction",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_InputAction_InputId_GroupId",
                table: "InputAction",
                columns: new[] { "InputId", "GroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InputAction_InputId_RelayId",
                table: "InputAction",
                columns: new[] { "InputId", "RelayId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InputAction_RelayId",
                table: "InputAction",
                column: "RelayId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InputAction");
        }
    }
}
