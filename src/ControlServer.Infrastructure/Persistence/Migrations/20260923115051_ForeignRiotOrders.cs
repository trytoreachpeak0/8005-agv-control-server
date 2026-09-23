using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // EF migration generator emits inline metadata arrays.

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ForeignRiotOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ForeignRiotOrders",
                columns: table => new
                {
                    RiotOrderId = table.Column<string>(type: "TEXT", nullable: false),
                    UpperId = table.Column<string>(type: "TEXT", nullable: true),
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceKey = table.Column<string>(type: "TEXT", nullable: false),
                    Ownership = table.Column<string>(type: "TEXT", nullable: false),
                    OwnershipBasis = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    OrderStateAtDetection = table.Column<int>(type: "INTEGER", nullable: true),
                    DetectedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenRunningAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CancelDecidedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CancelCommandAuditId = table.Column<string>(type: "TEXT", nullable: true),
                    CancelSentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CancelCallDisposition = table.Column<string>(type: "TEXT", nullable: true),
                    CancelResult = table.Column<string>(type: "TEXT", nullable: true),
                    CancelResultAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    EndedOrderState = table.Column<int>(type: "INTEGER", nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForeignRiotOrders", x => x.RiotOrderId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ForeignRiotOrders_AgvId_State",
                table: "ForeignRiotOrders",
                columns: new[] { "AgvId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_ForeignRiotOrders_CancelCommandAuditId",
                table: "ForeignRiotOrders",
                column: "CancelCommandAuditId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ForeignRiotOrders");
        }
    }
}
