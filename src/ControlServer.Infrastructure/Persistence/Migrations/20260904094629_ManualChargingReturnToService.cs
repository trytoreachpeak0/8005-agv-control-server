using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManualChargingReturnToService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ManualChargingReturnToServiceRequests",
                columns: table => new
                {
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    RequestContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    AdministratorId = table.Column<string>(type: "TEXT", nullable: false),
                    AdministratorRole = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    ObservedBatteryPercent = table.Column<double>(type: "REAL", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    ProblemReasonCode = table.Column<string>(type: "TEXT", nullable: true),
                    ProblemFieldPath = table.Column<string>(type: "TEXT", nullable: true),
                    ProblemDisplayMessage = table.Column<string>(type: "TEXT", nullable: true),
                    VehicleBusinessStateRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualChargingReturnToServiceRequests", x => x.RequestId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManualChargingReturnToServiceRequests_RequestMessageId",
                table: "ManualChargingReturnToServiceRequests",
                column: "RequestMessageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManualChargingReturnToServiceRequests");
        }
    }
}
