using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Custos.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class UploadDiscordUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "discord_user_id",
                table: "uploads",
                type: "bigint",
                nullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "discord_user_id", table: "uploads");
        }
    }
}
