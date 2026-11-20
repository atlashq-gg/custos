using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Custos.Server.Storage.Migrations
{
    /// <inheritdoc />
    public partial class LabelsAndMapData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "generated_label",
                table: "structures",
                type: "text",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "label",
                table: "structures",
                type: "text",
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "map_regions",
                columns: table => new
                {
                    war_id = table.Column<Guid>(type: "uuid", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    static_etag = table.Column<string>(type: "text", nullable: true),
                    fetched_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_map_regions", x => new { x.war_id, x.region });
                    table.ForeignKey(
                        name: "fk_map_regions_wars_war_id",
                        column: x => x.war_id,
                        principalTable: "wars",
                        principalColumn: "war_id"
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "map_items",
                columns: table => new
                {
                    war_id = table.Column<Guid>(type: "uuid", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    item_index = table.Column<int>(type: "integer", nullable: false),
                    icon_type = table.Column<int>(type: "integer", nullable: false),
                    x = table.Column<float>(type: "real", nullable: false),
                    y = table.Column<float>(type: "real", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_map_items", x => new
                    {
                        x.war_id,
                        x.region,
                        x.item_index,
                    });
                    table.ForeignKey(
                        name: "fk_map_items_map_regions_war_id_region",
                        columns: x => new { x.war_id, x.region },
                        principalTable: "map_regions",
                        principalColumns: new[] { "war_id", "region" },
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "map_places",
                columns: table => new
                {
                    war_id = table.Column<Guid>(type: "uuid", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    place_index = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    x = table.Column<float>(type: "real", nullable: false),
                    y = table.Column<float>(type: "real", nullable: false),
                    major = table.Column<bool>(type: "boolean", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_map_places", x => new
                    {
                        x.war_id,
                        x.region,
                        x.place_index,
                    });
                    table.ForeignKey(
                        name: "fk_map_places_map_regions_war_id_region",
                        columns: x => new { x.war_id, x.region },
                        principalTable: "map_regions",
                        principalColumns: new[] { "war_id", "region" },
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            // EF Core cannot declare an index on an expression. The label that shows is unique in
            // one guild and one war, without regard to case. A row of 0.1.0 has no label until
            // LabelBackfill generates it, and PostgreSQL permits more than one null.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_structures_shown_label ON structures "
                    + "(guild_id, war_id, lower(coalesce(label, generated_label)))"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX ix_structures_shown_label");

            migrationBuilder.DropTable(name: "map_items");

            migrationBuilder.DropTable(name: "map_places");

            migrationBuilder.DropTable(name: "map_regions");

            migrationBuilder.DropColumn(name: "generated_label", table: "structures");

            migrationBuilder.DropColumn(name: "label", table: "structures");
        }
    }
}
