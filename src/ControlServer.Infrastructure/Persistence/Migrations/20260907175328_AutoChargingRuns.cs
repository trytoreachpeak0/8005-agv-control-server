using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutoChargingRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutoChargingRuns",
                columns: table => new
                {
                    ChargingRunId = table.Column<string>(type: "TEXT", nullable: false),
                    VehicleKey = table.Column<string>(type: "TEXT", nullable: false),
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    Stage = table.Column<string>(type: "TEXT", nullable: false),
                    ChargerStationId = table.Column<string>(type: "TEXT", nullable: false),
                    ChargerStationRiotId = table.Column<int>(type: "INTEGER", nullable: false),
                    MovementLegId = table.Column<string>(type: "TEXT", nullable: false),
                    UpperId = table.Column<string>(type: "TEXT", nullable: false),
                    TriggeredAtBatteryPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    ReleasedAtBatteryPercent = table.Column<int>(type: "INTEGER", nullable: true),
                    BlockReasonCode = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutoChargingRuns", x => x.ChargingRunId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutoChargingRuns_UpperId",
                table: "AutoChargingRuns",
                column: "UpperId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutoChargingRuns");
        }
    }
}
