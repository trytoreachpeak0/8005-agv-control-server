using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MapNameBaselines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MapNameBaselines",
                columns: table => new
                {
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    EstablishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PendingName = table.Column<string>(type: "TEXT", nullable: true),
                    PendingSince = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AcceptedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapNameBaselines", x => x.MapId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MapNameBaselines");
        }
    }
}
