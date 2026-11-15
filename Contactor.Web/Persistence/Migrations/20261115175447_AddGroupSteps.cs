using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contactor.Web.Persistence.Migrations
{
    /// <summary>
    /// A group becomes a chain of steps, each with one or more relays. A group made before keeps working as it did:
    /// each of its relays becomes a step of its own, with the relay's name in the group and its times, and a command
    /// its sequence was waiting for waits for the same step.
    /// </summary>
    public partial class AddGroupSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RelayGroupStep",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    StartDelaySec = table.Column<int>(type: "INTEGER", nullable: false),
                    RunOnSec = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelayGroupStep", x => x.Id);
                    table.UniqueConstraint("AK_RelayGroupStep_Id_GroupId", x => new { x.Id, x.GroupId });
                    table.CheckConstraint("CK_RelayGroupStep_Position", "\"Position\" BETWEEN 1 AND 64");
                    table.CheckConstraint("CK_RelayGroupStep_RunOnSec", "\"RunOnSec\" BETWEEN 0 AND 1800");
                    table.CheckConstraint("CK_RelayGroupStep_StartDelaySec", "\"StartDelaySec\" BETWEEN 1 AND 60");
                    table.ForeignKey(
                        name: "FK_RelayGroupStep_RelayGroup_GroupId",
                        column: x => x.GroupId,
                        principalTable: "RelayGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RelayGroupStep_GroupId_Position",
                table: "RelayGroupStep",
                columns: new[] { "GroupId", "Position" },
                unique: true);

            migrationBuilder.AddColumn<int>(
                name: "StepId",
                table: "RelayGroupMember",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Order",
                table: "RelayGroupMember",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            // A step for each relay of a group made before, with its name in the group and its times; the relay is
            // the step's only one.
            migrationBuilder.Sql(
                """
                INSERT INTO "RelayGroupStep" ("GroupId", "Position", "Name", "StartDelaySec", "RunOnSec")
                SELECT "GroupId", "Position", "RoleName", "StartDelaySec", "RunOnSec" FROM "RelayGroupMember";
                UPDATE "RelayGroupMember" SET "StepId" = (
                    SELECT s."Id" FROM "RelayGroupStep" AS s
                    WHERE s."GroupId" = "RelayGroupMember"."GroupId" AND s."Position" = "RelayGroupMember"."Position");
                """);

            // The relay a command was waiting for is its step now.
            migrationBuilder.RenameColumn(
                name: "PendingPosition",
                table: "GroupRuntime",
                newName: "PendingStep");

            migrationBuilder.AddColumn<int>(
                name: "PendingRelayId",
                table: "GroupRuntime",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StopRelayIds",
                table: "GroupRuntime",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.DropIndex(
                name: "IX_RelayGroupMember_GroupId_Position",
                table: "RelayGroupMember");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RelayGroupMember_Position",
                table: "RelayGroupMember");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RelayGroupMember_RunOnSec",
                table: "RelayGroupMember");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RelayGroupMember_StartDelaySec",
                table: "RelayGroupMember");

            migrationBuilder.DropColumn(
                name: "Position",
                table: "RelayGroupMember");

            migrationBuilder.DropColumn(
                name: "RoleName",
                table: "RelayGroupMember");

            migrationBuilder.DropColumn(
                name: "StartDelaySec",
                table: "RelayGroupMember");

            migrationBuilder.DropColumn(
                name: "RunOnSec",
                table: "RelayGroupMember");

            migrationBuilder.CreateIndex(
                name: "IX_RelayGroupMember_StepId_GroupId",
                table: "RelayGroupMember",
                columns: new[] { "StepId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_RelayGroupMember_StepId_Order",
                table: "RelayGroupMember",
                columns: new[] { "StepId", "Order" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RelayGroupMember_Order",
                table: "RelayGroupMember",
                sql: "\"Order\" BETWEEN 1 AND 64");

            migrationBuilder.AddForeignKey(
                name: "FK_RelayGroupMember_RelayGroupStep_StepId_GroupId",
                table: "RelayGroupMember",
                columns: new[] { "StepId", "GroupId" },
                principalTable: "RelayGroupStep",
                principalColumns: new[] { "Id", "GroupId" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <summary>
        /// Back to one relay per step: each relay gets a place of its own, in the order of the steps and then of the relays
        /// in each step, its step's times, and as its name in the group its step's name (its own name when its step had
        /// more). A group with a step of several relays forgets the command its sequence was waiting for.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The relays' table is rebuilt here by hand, not by EF Core's rebuild: that runs at the end of the
            // migration, after the steps' table is dropped, and dropping it would delete every relay of every group
            // through the foreign key's cascade first.
            migrationBuilder.Sql(
                """
                UPDATE "GroupRuntime" SET "PendingStep" = NULL, "PendingRelayId" = NULL, "PendingOn" = 0, "PendingSinceUtc" = NULL
                WHERE "GroupId" IN (SELECT "GroupId" FROM "RelayGroupMember" GROUP BY "StepId" HAVING COUNT(*) > 1);
                CREATE TABLE "ef_down_RelayGroupMember" (
                    "GroupId" INTEGER NOT NULL,
                    "RelayId" INTEGER NOT NULL,
                    "Position" INTEGER NOT NULL,
                    "RoleName" TEXT NOT NULL,
                    "RunOnSec" INTEGER NOT NULL,
                    "StartDelaySec" INTEGER NOT NULL,
                    CONSTRAINT "PK_RelayGroupMember" PRIMARY KEY ("GroupId", "RelayId"),
                    CONSTRAINT "CK_RelayGroupMember_Position" CHECK ("Position" BETWEEN 1 AND 64),
                    CONSTRAINT "CK_RelayGroupMember_RunOnSec" CHECK ("RunOnSec" BETWEEN 0 AND 1800),
                    CONSTRAINT "CK_RelayGroupMember_StartDelaySec" CHECK ("StartDelaySec" BETWEEN 1 AND 60),
                    CONSTRAINT "FK_RelayGroupMember_RelayGroup_GroupId" FOREIGN KEY ("GroupId") REFERENCES "RelayGroup" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_RelayGroupMember_Relay_RelayId" FOREIGN KEY ("RelayId") REFERENCES "Relay" ("Id") ON DELETE RESTRICT
                );
                INSERT INTO "ef_down_RelayGroupMember" ("GroupId", "RelayId", "Position", "RoleName", "RunOnSec", "StartDelaySec")
                SELECT m."GroupId", m."RelayId",
                    s."Position"
                        + (SELECT COUNT(*) FROM "RelayGroupMember" AS m2 JOIN "RelayGroupStep" AS s2 ON s2."Id" = m2."StepId"
                           WHERE s2."GroupId" = s."GroupId" AND s2."Position" < s."Position")
                        - (SELECT COUNT(*) FROM "RelayGroupStep" AS s3 WHERE s3."GroupId" = s."GroupId" AND s3."Position" < s."Position")
                        + (SELECT COUNT(*) FROM "RelayGroupMember" AS m4 WHERE m4."StepId" = s."Id" AND m4."Order" < m."Order"),
                    CASE WHEN (SELECT COUNT(*) FROM "RelayGroupMember" AS m5 WHERE m5."StepId" = s."Id") = 1
                        THEN s."Name"
                        ELSE (SELECT r."Name" FROM "Relay" AS r WHERE r."Id" = m."RelayId") END,
                    s."RunOnSec", s."StartDelaySec"
                FROM "RelayGroupMember" AS m JOIN "RelayGroupStep" AS s ON s."Id" = m."StepId";
                DROP TABLE "RelayGroupMember";
                ALTER TABLE "ef_down_RelayGroupMember" RENAME TO "RelayGroupMember";
                CREATE UNIQUE INDEX "IX_RelayGroupMember_GroupId_Position" ON "RelayGroupMember" ("GroupId", "Position");
                CREATE UNIQUE INDEX "IX_RelayGroupMember_RelayId" ON "RelayGroupMember" ("RelayId");
                """);

            migrationBuilder.DropTable(
                name: "RelayGroupStep");

            migrationBuilder.DropColumn(
                name: "PendingRelayId",
                table: "GroupRuntime");

            migrationBuilder.DropColumn(
                name: "StopRelayIds",
                table: "GroupRuntime");

            migrationBuilder.RenameColumn(
                name: "PendingStep",
                table: "GroupRuntime",
                newName: "PendingPosition");
        }
    }
}
