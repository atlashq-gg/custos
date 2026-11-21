using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Custos.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class GuildSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "guild_settings",
                columns: table => new
                {
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    updated_by = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guild_settings", x => x.guild_id);
                }
            );

            migrationBuilder
                .CreateIndex(
                    name: "ix_guild_settings_document",
                    table: "guild_settings",
                    column: "document"
                )
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "jsonb_path_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "guild_settings");
        }
    }
}
