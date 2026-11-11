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
