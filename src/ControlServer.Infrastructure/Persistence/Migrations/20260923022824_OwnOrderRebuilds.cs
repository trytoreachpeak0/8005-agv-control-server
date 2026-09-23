using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // EF migration generator emits inline metadata arrays.

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OwnOrderRebuilds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OwnOrderRebuilds",
                columns: table => new
                {
                    RebuildId = table.Column<string>(type: "TEXT", nullable: false),
                    JourneyId = table.Column<string>(type: "TEXT", nullable: false),
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    VehicleKey = table.Column<string>(type: "TEXT", nullable: false),
                    StopId = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    EndedUpperId = table.Column<string>(type: "TEXT", nullable: false),
                    EndedOrderId = table.Column<string>(type: "TEXT", nullable: true),
                    EndedOrderState = table.Column<int>(type: "INTEGER", nullable: true),
                    IncidentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    OperatorId = table.Column<string>(type: "TEXT", nullable: true),
                    NewUpperId = table.Column<string>(type: "TEXT", nullable: false),
                    NewMovementLegId = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    WaitingReason = table.Column<string>(type: "TEXT", nullable: true),
                    WaitingSince = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RebuiltAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    StoppedReason = table.Column<string>(type: "TEXT", nullable: true),
                    StoppedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnOrderRebuilds", x => x.RebuildId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OwnOrderRebuilds_DemandId",
                table: "OwnOrderRebuilds",
                column: "DemandId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnOrderRebuilds_EndedUpperId",
                table: "OwnOrderRebuilds",
                column: "EndedUpperId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OwnOrderRebuilds_JourneyId_StopId",
                table: "OwnOrderRebuilds",
                columns: new[] { "JourneyId", "StopId" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnOrderRebuilds_NewUpperId",
                table: "OwnOrderRebuilds",
                column: "NewUpperId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OwnOrderRebuilds");
        }
    }
}
