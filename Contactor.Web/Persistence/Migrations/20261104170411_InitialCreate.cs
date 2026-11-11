using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contactor.Web.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RelayId = table.Column<int>(type: "INTEGER", nullable: true),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 254, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Details = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Input",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ClosedText = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    OpenText = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    NotifyOnOpen = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyOnClose = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Input", x => x.Id);
                    table.CheckConstraint("CK_Input_Id", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "Relay",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxOnMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Relay", x => x.Id);
                    table.CheckConstraint("CK_Relay_Id", "\"Id\" BETWEEN 1 AND 8");
                });

            migrationBuilder.CreateTable(
                name: "User",
                columns: table => new
                {
                    Email = table.Column<string>(type: "TEXT", maxLength: 254, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_User", x => x.Email);
                });

            migrationBuilder.CreateTable(
                name: "RelayRuntime",
                columns: table => new
                {
                    RelayId = table.Column<int>(type: "INTEGER", nullable: false),
                    OnSinceUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AutoOffAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TurnedOnBy = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelayRuntime", x => x.RelayId);
                    table.ForeignKey(
                        name: "FK_RelayRuntime_Relay_RelayId",
                        column: x => x.RelayId,
                        principalTable: "Relay",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Schedule",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RelayId = table.Column<int>(type: "INTEGER", nullable: false),
                    Days = table.Column<int>(type: "INTEGER", nullable: false),
                    OnTime = table.Column<string>(type: "TEXT", maxLength: 5, nullable: false),
                    OffTime = table.Column<string>(type: "TEXT", maxLength: 5, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 254, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Schedule", x => x.Id);
                    table.CheckConstraint("CK_Schedule_Days", "\"Days\" BETWEEN 1 AND 127");
                    table.ForeignKey(
                        name: "FK_Schedule_Relay_RelayId",
                        column: x => x.RelayId,
                        principalTable: "Relay",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventLog_RelayId_TimestampUtc",
                table: "EventLog",
                columns: new[] { "RelayId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EventLog_TimestampUtc",
                table: "EventLog",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Schedule_RelayId",
                table: "Schedule",
                column: "RelayId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventLog");

            migrationBuilder.DropTable(
                name: "Input");

            migrationBuilder.DropTable(
                name: "RelayRuntime");

            migrationBuilder.DropTable(
                name: "Schedule");

            migrationBuilder.DropTable(
                name: "User");

            migrationBuilder.DropTable(
                name: "Relay");
        }
    }
}
