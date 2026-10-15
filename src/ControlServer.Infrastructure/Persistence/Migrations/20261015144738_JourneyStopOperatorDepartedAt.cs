using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class JourneyStopOperatorDepartedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OperatorDepartedAt",
                table: "JourneyStops",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQLite's own DROP COLUMN, not migrationBuilder.DropColumn: EF's SQLite provider rebuilds the table for that,
            // in the model's column order, so the table would come back with its columns reordered and a different stored
            // definition. The column is the table's last one and nothing indexes it, so dropping it in place gives back the
            // table exactly as 20261013041134_ProtocolInboxTypeNewestFirstIndex left it (JourneyStopOperatorDepartedAtMigrationTests).
            migrationBuilder.Sql("""ALTER TABLE "JourneyStops" DROP COLUMN "OperatorDepartedAt";""");
        }
    }
}
