using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contactor.Web.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowUpTo64Relays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RelayGroupMember_Position",
                table: "RelayGroupMember");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Relay_Id",
                table: "Relay");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RelayGroupMember_Position",
                table: "RelayGroupMember",
                sql: "\"Position\" BETWEEN 1 AND 64");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Relay_Id",
                table: "Relay",
                sql: "\"Id\" BETWEEN 1 AND 64");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rolling back means going back to a VM201's 8 relays. SQLite rebuilds the tables to restore the
            // constraints below, which fails on any row above 8, so those rows go first: group members at a
            // position or on a relay above 8, then everything that refers to a relay above 8 (its schedules
            // and runtime row), then the relays themselves. EventLog.RelayId is no foreign key and stays, as
            // events outlive what they concern. EF Core can't express a delete in a migration, hence plain SQL.
            migrationBuilder.Sql("DELETE FROM \"RelayGroupMember\" WHERE \"Position\" > 8 OR \"RelayId\" > 8;");
            migrationBuilder.Sql("DELETE FROM \"Schedule\" WHERE \"RelayId\" > 8;");
            migrationBuilder.Sql("DELETE FROM \"RelayRuntime\" WHERE \"RelayId\" > 8;");
            migrationBuilder.Sql("DELETE FROM \"Relay\" WHERE \"Id\" > 8;");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RelayGroupMember_Position",
                table: "RelayGroupMember");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Relay_Id",
                table: "Relay");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RelayGroupMember_Position",
                table: "RelayGroupMember",
                sql: "\"Position\" BETWEEN 1 AND 8");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Relay_Id",
                table: "Relay",
                sql: "\"Id\" BETWEEN 1 AND 8");
        }
    }
}
