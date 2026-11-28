using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optio.Storage.Migrations
{
    /// <inheritdoc />
    public partial class OperatorLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "operator_limits",
                columns: table => new
                {
                    scope = table.Column<string>(
                        type: "character varying(10)",
                        maxLength: 10,
                        nullable: false
                    ),
                    target_id = table.Column<long>(type: "bigint", nullable: false),
                    tokens_per_hour = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    updated_by = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_operator_limits", x => new { x.scope, x.target_id });
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "operator_limits");
        }
    }
}
