using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Contactor.Web.Persistence.Migrations
{
    /// <summary>
    /// Every installation in use ran Cloudflare Tunnel and Cloudflare Access: saved as their choice, so an empty one
    /// means "not chosen yet" from here on, and opens the setup guide behind its setup code.
    /// </summary>
    public partial class FillInstallationMethods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE "Installation" SET "AccessType" = 'cloudflare' WHERE "AccessType" IS NULL;""");
            migrationBuilder.Sql("""UPDATE "Installation" SET "SignInType" = 'cloudflare-access' WHERE "SignInType" IS NULL;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Empty meant Cloudflare before: nothing to undo.
        }
    }
}
