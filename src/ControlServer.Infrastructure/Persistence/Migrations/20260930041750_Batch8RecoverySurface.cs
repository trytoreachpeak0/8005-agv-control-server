using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Batch8RecoverySurface : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HandedOverAt",
                table: "RecoveryWorkflows",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HandoffReceiverName",
                table: "RecoveryWorkflows",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HandoffSublot",
                table: "RecoveryWorkflows",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosedReason",
                table: "ExceptionRecoverySessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SlotDoorHolds",
                columns: table => new
                {
                    HoldId = table.Column<string>(type: "TEXT", nullable: false),
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    SlotsJson = table.Column<string>(type: "TEXT", nullable: false),
                    HeldAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ReleasedByActionId = table.Column<string>(type: "TEXT", nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlotDoorHolds", x => x.HoldId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SlotDoorHolds_AgvId",
                table: "SlotDoorHolds",
                column: "AgvId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SlotDoorHolds");

            migrationBuilder.DropColumn(
                name: "HandedOverAt",
                table: "RecoveryWorkflows");

            migrationBuilder.DropColumn(
                name: "HandoffReceiverName",
                table: "RecoveryWorkflows");

            migrationBuilder.DropColumn(
                name: "HandoffSublot",
                table: "RecoveryWorkflows");

            migrationBuilder.DropColumn(
                name: "ClosedReason",
                table: "ExceptionRecoverySessions");
        }
    }
}
