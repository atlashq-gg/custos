using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Custos.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // EF Core cannot declare a partitioned table. WarRegistry creates one partition for each war.
            migrationBuilder.Sql("""
                CREATE TABLE stockpile_changes (
                    guild_id     bigint      NOT NULL,
                    war_id       uuid        NOT NULL,
                    stockpile_id uuid        NOT NULL,
                    observed_at  timestamptz NOT NULL,
                    last_seen_at timestamptz NOT NULL,
                    upload_id    uuid        NOT NULL,
                    entries      jsonb       NOT NULL,
                    CONSTRAINT pk_stockpile_changes PRIMARY KEY (guild_id, war_id, stockpile_id, observed_at)
                ) PARTITION BY LIST (war_id);

                CREATE TABLE structure_changes (
                    guild_id     bigint      NOT NULL,
                    war_id       uuid        NOT NULL,
                    structure_id uuid        NOT NULL,
                    observed_at  timestamptz NOT NULL,
                    last_seen_at timestamptz NOT NULL,
                    upload_id    uuid        NOT NULL,
                    code_name    text        NOT NULL,
                    details      jsonb       NOT NULL,
                    CONSTRAINT pk_structure_changes PRIMARY KEY (guild_id, war_id, structure_id, observed_at)
                ) PARTITION BY LIST (war_id);
                """);

            migrationBuilder.CreateTable(
                name: "wars",
                columns: table => new
                {
                    war_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shard = table.Column<string>(type: "text", nullable: false),
                    war_number = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    purged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wars", x => x.war_id);
                });

            migrationBuilder.CreateTable(
                name: "structures",
                columns: table => new
                {
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    war_id = table.Column<Guid>(type: "uuid", nullable: false),
                    structure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    x = table.Column<float>(type: "real", nullable: false),
                    y = table.Column<float>(type: "real", nullable: false),
                    code_name = table.Column<string>(type: "text", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    details = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_structures", x => new { x.guild_id, x.war_id, x.structure_id });
                    table.ForeignKey(
                        name: "fk_structures_wars_war_id",
                        column: x => x.war_id,
                        principalTable: "wars",
                        principalColumn: "war_id");
                });

            migrationBuilder.CreateTable(
                name: "uploads",
                columns: table => new
                {
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    war_id = table.Column<Guid>(type: "uuid", nullable: false),
                    upload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    size_bytes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_uploads", x => new { x.guild_id, x.war_id, x.upload_id });
                    table.ForeignKey(
                        name: "fk_uploads_wars_war_id",
                        column: x => x.war_id,
                        principalTable: "wars",
                        principalColumn: "war_id");
                });

            migrationBuilder.CreateTable(
                name: "stockpiles",
                columns: table => new
                {
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    war_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stockpile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    structure_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_hash = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stockpiles", x => new { x.guild_id, x.war_id, x.stockpile_id });
                    table.ForeignKey(
                        name: "fk_stockpiles_structures_guild_id_war_id_structure_id",
                        columns: x => new { x.guild_id, x.war_id, x.structure_id },
                        principalTable: "structures",
                        principalColumns: new[] { "guild_id", "war_id", "structure_id" });
                });

            migrationBuilder.CreateTable(
                name: "stockpile_entries",
                columns: table => new
                {
                    guild_id = table.Column<long>(type: "bigint", nullable: false),
                    war_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stockpile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    code_name = table.Column<string>(type: "text", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stockpile_entries", x => new { x.guild_id, x.war_id, x.stockpile_id, x.category, x.code_name });
                    table.ForeignKey(
                        name: "fk_stockpile_entries_stockpiles_guild_id_war_id_stockpile_id",
                        columns: x => new { x.guild_id, x.war_id, x.stockpile_id },
                        principalTable: "stockpiles",
                        principalColumns: new[] { "guild_id", "war_id", "stockpile_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stockpiles_guild_id_war_id_structure_id",
                table: "stockpiles",
                columns: new[] { "guild_id", "war_id", "structure_id" });

            migrationBuilder.CreateIndex(
                name: "ix_structures_war_id",
                table: "structures",
                column: "war_id");

            migrationBuilder.CreateIndex(
                name: "ix_uploads_war_id",
                table: "uploads",
                column: "war_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stockpile_changes");

            migrationBuilder.DropTable(
                name: "stockpile_entries");

            migrationBuilder.DropTable(
                name: "structure_changes");

            migrationBuilder.DropTable(
                name: "uploads");

            migrationBuilder.DropTable(
                name: "stockpiles");

            migrationBuilder.DropTable(
                name: "structures");

            migrationBuilder.DropTable(
                name: "wars");
        }
    }
}
