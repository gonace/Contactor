using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contactor.Web.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRelayGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GroupId",
                table: "EventLog",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RelayGroup",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Icon = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    MaxOnMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelayGroup", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GroupRuntime",
                columns: table => new
                {
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    Phase = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TargetLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    NextStepAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PendingPosition = table.Column<int>(type: "INTEGER", nullable: true),
                    PendingOn = table.Column<bool>(type: "INTEGER", nullable: false),
                    PendingSinceUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    OnSinceUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AutoOffAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TurnedOnBy = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupRuntime", x => x.GroupId);
                    table.ForeignKey(
                        name: "FK_GroupRuntime_RelayGroup_GroupId",
                        column: x => x.GroupId,
                        principalTable: "RelayGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RelayGroupMember",
                columns: table => new
                {
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    RelayId = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    RoleName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    StartDelaySec = table.Column<int>(type: "INTEGER", nullable: false),
                    RunOnSec = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelayGroupMember", x => new { x.GroupId, x.RelayId });
                    table.CheckConstraint("CK_RelayGroupMember_Position", "\"Position\" BETWEEN 1 AND 8");
                    table.CheckConstraint("CK_RelayGroupMember_RunOnSec", "\"RunOnSec\" BETWEEN 0 AND 1800");
                    table.CheckConstraint("CK_RelayGroupMember_StartDelaySec", "\"StartDelaySec\" BETWEEN 1 AND 60");
                    table.ForeignKey(
                        name: "FK_RelayGroupMember_RelayGroup_GroupId",
                        column: x => x.GroupId,
                        principalTable: "RelayGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RelayGroupMember_Relay_RelayId",
                        column: x => x.RelayId,
                        principalTable: "Relay",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventLog_GroupId_TimestampUtc",
                table: "EventLog",
                columns: new[] { "GroupId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RelayGroupMember_GroupId_Position",
                table: "RelayGroupMember",
                columns: new[] { "GroupId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RelayGroupMember_RelayId",
                table: "RelayGroupMember",
                column: "RelayId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupRuntime");

            migrationBuilder.DropTable(
                name: "RelayGroupMember");

            migrationBuilder.DropTable(
                name: "RelayGroup");

            migrationBuilder.DropIndex(
                name: "IX_EventLog_GroupId_TimestampUtc",
                table: "EventLog");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "EventLog");
        }
    }
}
