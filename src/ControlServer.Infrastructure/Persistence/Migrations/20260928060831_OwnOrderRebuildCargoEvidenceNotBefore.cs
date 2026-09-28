using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OwnOrderRebuildCargoEvidenceNotBefore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CargoEvidenceNotBefore",
                table: "OwnOrderRebuilds",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VehicleHeldAt",
                table: "OwnOrderRebuilds",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CargoEvidenceNotBefore",
                table: "OwnOrderRebuilds");

            migrationBuilder.DropColumn(
                name: "VehicleHeldAt",
                table: "OwnOrderRebuilds");
        }
    }
}
