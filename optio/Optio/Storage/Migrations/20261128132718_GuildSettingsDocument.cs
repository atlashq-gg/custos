using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optio.Storage.Migrations
{
    /// <summary>
    /// Replaces the column <c>prompt</c> with the settings document. The prompt of each guild goes
    /// to <c>Prompts.Guild</c>. The row version is the system column <c>xmin</c>, so it needs no
    /// column.
    /// </summary>
    public partial class GuildSettingsDocument : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "document",
                table: "guild_settings",
                type: "jsonb",
                nullable: true
            );
            migrationBuilder.Sql(
                """
                UPDATE guild_settings SET document = jsonb_build_object(
                    'SchemaVersion', 1,
                    'Access', jsonb_build_object('QueryRoleIds', jsonb_build_array()),
                    'Prompts', jsonb_build_object('Guild', prompt, 'Roles', jsonb_build_object()),
                    'Limits', jsonb_build_object(
                        'Roles', jsonb_build_object(), 'Users', jsonb_build_object()));
                """
            );
            migrationBuilder.AlterColumn<string>(
                name: "document",
                table: "guild_settings",
                type: "jsonb",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldNullable: true
            );
            migrationBuilder.DropColumn(name: "prompt", table: "guild_settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "prompt",
                table: "guild_settings",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true
            );
            migrationBuilder.Sql(
                "UPDATE guild_settings SET prompt = document -> 'Prompts' ->> 'Guild';"
            );
            migrationBuilder.DropColumn(name: "document", table: "guild_settings");
        }
    }
}
