using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contactor.Web.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MoveToBoards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Board",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 253, nullable: true),
                    Port = table.Column<int>(type: "INTEGER", nullable: true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Password = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Board", x => x.Id);
                    table.CheckConstraint("CK_Board_Address", "(\"Host\" IS NULL) = (\"Port\" IS NULL)");
                    table.CheckConstraint("CK_Board_Port", "\"Port\" BETWEEN 1 AND 65535");
                });

            migrationBuilder.CreateTable(
                name: "BoardRuntime",
                columns: table => new
                {
                    BoardId = table.Column<int>(type: "INTEGER", nullable: false),
                    LastTickUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastContactUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LongGapEndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardRuntime", x => x.BoardId);
                    table.ForeignKey(
                        name: "FK_BoardRuntime_Board_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Board",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // The board the app ran becomes Board 1, when there was one: a model chosen in the setup guide, or rows of
            // a board (relays, inputs, groups, a saved address) from before the guide chose the model. Its name is the
            // model's, given at start (BoardChoice); a model left empty here is chosen there as before (Board:Type, or
            // the VM201).
            migrationBuilder.Sql("""
                INSERT INTO "Board" ("Id", "Name", "Type", "Host", "Port", "Username", "Password", "SortOrder")
                SELECT 1, '', coalesce((SELECT "BoardType" FROM "Installation" WHERE "Id" = 1), ''),
                       s."Host", s."Port", s."Username", s."Password", 1
                FROM (SELECT 1) LEFT JOIN "BoardSetting" AS s ON s."Id" = 1
                WHERE EXISTS (SELECT 1 FROM "Installation" WHERE "BoardType" IS NOT NULL)
                   OR EXISTS (SELECT 1 FROM "Relay")
                   OR EXISTS (SELECT 1 FROM "Input")
                   OR EXISTS (SELECT 1 FROM "RelayGroup")
                   OR EXISTS (SELECT 1 FROM "BoardSetting");
                """);
            migrationBuilder.Sql("""
                INSERT INTO "BoardRuntime" ("BoardId", "LastTickUtc", "LastContactUtc", "LongGapEndedUtc")
                SELECT 1,
                       (SELECT "LastTickUtc" FROM "SchedulerState" WHERE "Id" = 1),
                       (SELECT "LastContactUtc" FROM "BoardWatchState" WHERE "Id" = 1),
                       (SELECT "LongGapEndedUtc" FROM "BoardWatchState" WHERE "Id" = 1)
                WHERE EXISTS (SELECT 1 FROM "Board" WHERE "Id" = 1);
                """);

            migrationBuilder.AddColumn<int>(
                name: "BoardId",
                table: "RelayGroup",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BoardId",
                table: "Relay",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Channel",
                table: "Relay",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BoardId",
                table: "Input",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Channel",
                table: "Input",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BoardId",
                table: "EventLog",
                type: "INTEGER",
                nullable: true);

            // Everything was on that board, and each relay's and input's id was its channel; every event concerns it.
            migrationBuilder.Sql("""UPDATE "Relay" SET "BoardId" = 1, "Channel" = "Id";""");
            migrationBuilder.Sql("""UPDATE "Input" SET "BoardId" = 1, "Channel" = "Id";""");
            migrationBuilder.Sql("""UPDATE "RelayGroup" SET "BoardId" = 1;""");
            migrationBuilder.Sql("""UPDATE "EventLog" SET "BoardId" = 1 WHERE EXISTS (SELECT 1 FROM "Board" WHERE "Id" = 1);""");

            migrationBuilder.CreateIndex(
                name: "IX_RelayGroup_BoardId",
                table: "RelayGroup",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_Relay_BoardId_Channel",
                table: "Relay",
                columns: new[] { "BoardId", "Channel" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Relay_Channel",
                table: "Relay",
                sql: "\"Channel\" BETWEEN 1 AND 64");

            migrationBuilder.CreateIndex(
                name: "IX_Input_BoardId_Channel",
                table: "Input",
                columns: new[] { "BoardId", "Channel" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Input_Channel",
                table: "Input",
                sql: "\"Channel\" BETWEEN 1 AND 64");

            migrationBuilder.CreateIndex(
                name: "IX_EventLog_BoardId_TimestampUtc",
                table: "EventLog",
                columns: new[] { "BoardId", "TimestampUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_EventLog_Board_BoardId",
                table: "EventLog",
                column: "BoardId",
                principalTable: "Board",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Input_Board_BoardId",
                table: "Input",
                column: "BoardId",
                principalTable: "Board",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Relay_Board_BoardId",
                table: "Relay",
                column: "BoardId",
                principalTable: "Board",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_RelayGroup_Board_BoardId",
                table: "RelayGroup",
                column: "BoardId",
                principalTable: "Board",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.DropTable(
                name: "BoardSetting");

            migrationBuilder.DropTable(
                name: "BoardWatchState");

            migrationBuilder.DropTable(
                name: "SchedulerState");

            migrationBuilder.DropColumn(
                name: "BoardType",
                table: "Installation");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // "Board" is dropped before the tables that point to it are rebuilt without it (EF Core rebuilds them at the
            // end), and dropping it with foreign keys on would delete their rows by cascade. The rebuild turns them on again.
            migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);

            migrationBuilder.AddColumn<string>(
                name: "BoardType",
                table: "Installation",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BoardSetting",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    Password = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardSetting", x => x.Id);
                    table.CheckConstraint("CK_BoardSetting_Id", "\"Id\" = 1");
                    table.CheckConstraint("CK_BoardSetting_Port", "\"Port\" BETWEEN 1 AND 65535");
                });

            migrationBuilder.CreateTable(
                name: "BoardWatchState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    LastContactUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LongGapEndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BoardWatchState", x => x.Id);
                    table.CheckConstraint("CK_BoardWatchState_Id", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "SchedulerState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    LastTickUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchedulerState", x => x.Id);
                    table.CheckConstraint("CK_SchedulerState_Id", "\"Id\" = 1");
                });

            // Back to one board in singletons: the first board's model, address, login and runtime.
            migrationBuilder.Sql("""
                INSERT INTO "Installation" ("Id") SELECT 1
                WHERE NOT EXISTS (SELECT 1 FROM "Installation") AND EXISTS (SELECT 1 FROM "Board" WHERE "Type" <> '');
                UPDATE "Installation" SET "BoardType" =
                    (SELECT nullif("Type", '') FROM "Board" ORDER BY "SortOrder", "Id" LIMIT 1);
                INSERT INTO "BoardSetting" ("Id", "Host", "Port", "Username", "Password")
                SELECT 1, "Host", "Port", "Username", "Password" FROM "Board"
                WHERE "Id" = (SELECT "Id" FROM "Board" ORDER BY "SortOrder", "Id" LIMIT 1) AND "Host" IS NOT NULL;
                INSERT INTO "SchedulerState" ("Id", "LastTickUtc")
                SELECT 1, "LastTickUtc" FROM "BoardRuntime"
                WHERE "BoardId" = (SELECT "Id" FROM "Board" ORDER BY "SortOrder", "Id" LIMIT 1);
                INSERT INTO "BoardWatchState" ("Id", "LastContactUtc", "LongGapEndedUtc")
                SELECT 1, "LastContactUtc", "LongGapEndedUtc" FROM "BoardRuntime"
                WHERE "BoardId" = (SELECT "Id" FROM "Board" ORDER BY "SortOrder", "Id" LIMIT 1);
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_EventLog_Board_BoardId",
                table: "EventLog");

            migrationBuilder.DropForeignKey(
                name: "FK_Input_Board_BoardId",
                table: "Input");

            migrationBuilder.DropForeignKey(
                name: "FK_Relay_Board_BoardId",
                table: "Relay");

            migrationBuilder.DropForeignKey(
                name: "FK_RelayGroup_Board_BoardId",
                table: "RelayGroup");

            migrationBuilder.DropTable(
                name: "BoardRuntime");

            migrationBuilder.DropTable(
                name: "Board");

            migrationBuilder.DropIndex(
                name: "IX_RelayGroup_BoardId",
                table: "RelayGroup");

            migrationBuilder.DropIndex(
                name: "IX_Relay_BoardId_Channel",
                table: "Relay");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Relay_Channel",
                table: "Relay");

            migrationBuilder.DropIndex(
                name: "IX_Input_BoardId_Channel",
                table: "Input");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Input_Channel",
                table: "Input");

            migrationBuilder.DropIndex(
                name: "IX_EventLog_BoardId_TimestampUtc",
                table: "EventLog");

            migrationBuilder.DropColumn(
                name: "BoardId",
                table: "RelayGroup");

            migrationBuilder.DropColumn(
                name: "BoardId",
                table: "Relay");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "Relay");

            migrationBuilder.DropColumn(
                name: "BoardId",
                table: "Input");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "Input");

            migrationBuilder.DropColumn(
                name: "BoardId",
                table: "EventLog");
        }
    }
}
