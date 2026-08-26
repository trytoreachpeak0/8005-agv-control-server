using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductionJourneyRuntimeWorker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JourneyBacklog",
                columns: table => new
                {
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    TransportDemandKey = table.Column<string>(type: "TEXT", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DemandCreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DecisionFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    ReasonCode = table.Column<string>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JourneyBacklog", x => x.DemandId);
                });

            migrationBuilder.CreateTable(
                name: "JourneyRuntimes",
                columns: table => new
                {
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    Stage = table.Column<string>(type: "TEXT", nullable: false),
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    VehicleKey = table.Column<string>(type: "TEXT", nullable: false),
                    AgvLifecycleGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    MapIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    DispatchZone = table.Column<string>(type: "TEXT", nullable: false),
                    RouteEvidenceId = table.Column<string>(type: "TEXT", nullable: false),
                    PickupStationId = table.Column<string>(type: "TEXT", nullable: false),
                    PickupStationRiotId = table.Column<int>(type: "INTEGER", nullable: false),
                    GateStationId = table.Column<string>(type: "TEXT", nullable: false),
                    GateStationRiotId = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpectedBasketCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetSlotsJson = table.Column<string>(type: "TEXT", nullable: false),
                    OperationSessionId = table.Column<string>(type: "TEXT", nullable: false),
                    PickupMovementLegId = table.Column<string>(type: "TEXT", nullable: false),
                    PickupUpperId = table.Column<string>(type: "TEXT", nullable: false),
                    GateMovementLegId = table.Column<string>(type: "TEXT", nullable: false),
                    GateUpperId = table.Column<string>(type: "TEXT", nullable: false),
                    DispatchGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    VehicleBusinessRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    WorklistRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    PlanRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    VehicleBusinessMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    WorklistMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    PlanMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    SublotRequestMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    LoadCommandMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    LoadSlotOperationAttemptId = table.Column<string>(type: "TEXT", nullable: false),
                    PreDepartureSafetyCheckMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    PreDepartureSafetyCheckId = table.Column<string>(type: "TEXT", nullable: false),
                    GateVehicleBusinessMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    GateWorklistMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    GatePlanMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    UnloadCommandMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    UnloadSlotOperationAttemptId = table.Column<string>(type: "TEXT", nullable: false),
                    ConsumedSublotMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    ConsumedSafetyResultMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    BlockReasonCode = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JourneyRuntimes", x => x.DemandId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JourneyBacklog_TransportDemandKey",
                table: "JourneyBacklog",
                column: "TransportDemandKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JourneyBacklog");

            migrationBuilder.DropTable(
                name: "JourneyRuntimes");
        }
    }
}
