using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // EF migration generator emits inline metadata arrays.

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Batch7MultiDemandJourneyPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "JourneyId",
                table: "VehicleDispatchLeases",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "JourneyId",
                table: "JourneyRuntimes",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CargoHoldingStartedAt",
                table: "JourneyRuntimes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FullSlotPositionsJson",
                table: "JourneyRuntimes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LoadingClosedReason",
                table: "JourneyRuntimes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LoadingPhaseState",
                table: "JourneyRuntimes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "YieldTriggeredAt",
                table: "JourneyRuntimes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "YieldTriggeredByVehicleKey",
                table: "JourneyRuntimes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StarvationEscalatedAt",
                table: "JourneyBacklog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "StarvationEscalationParameterVersion",
                table: "JourneyBacklog",
                type: "INTEGER",
                nullable: true);

            // The journey id must be in place before the key moves onto it: SQLite changes a primary key by rebuilding the
            // table, and the rebuild copies whatever the column holds. The rule is the one JourneyIdentity.ForAnchorDemand
            // applies to a newly accepted journey, so a back-filled row and a new one cannot be told apart.
            migrationBuilder.Sql("UPDATE JourneyRuntimes SET JourneyId = 'journey:' || DemandId;");
            migrationBuilder.Sql("UPDATE VehicleDispatchLeases SET JourneyId = 'journey:' || DemandId;");

            migrationBuilder.DropPrimaryKey(
                name: "PK_VehicleDispatchLeases",
                table: "VehicleDispatchLeases");

            migrationBuilder.DropPrimaryKey(
                name: "PK_JourneyRuntimes",
                table: "JourneyRuntimes");

            migrationBuilder.AddPrimaryKey(
                name: "PK_VehicleDispatchLeases",
                table: "VehicleDispatchLeases",
                column: "JourneyId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_JourneyRuntimes",
                table: "JourneyRuntimes",
                column: "JourneyId");

            migrationBuilder.CreateTable(
                name: "DispatchZoneParameters",
                columns: table => new
                {
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    DispatchZone = table.Column<string>(type: "TEXT", nullable: false),
                    EnRouteAdditionMaxPathCostIncrease = table.Column<long>(type: "INTEGER", nullable: true),
                    StarvationThresholdSeconds = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchZoneParameters", x => new { x.Version, x.DispatchZone });
                });

            migrationBuilder.CreateTable(
                name: "DispatchZoneParameterVersions",
                columns: table => new
                {
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<string>(type: "TEXT", nullable: true),
                    LoadedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchZoneParameterVersions", x => x.Version);
                });

            migrationBuilder.CreateTable(
                name: "JourneyDemands",
                columns: table => new
                {
                    JourneyId = table.Column<string>(type: "TEXT", nullable: false),
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    PickupStopId = table.Column<string>(type: "TEXT", nullable: false),
                    UnloadStopId = table.Column<string>(type: "TEXT", nullable: false),
                    ExpectedBasketCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetSlotsJson = table.Column<string>(type: "TEXT", nullable: false),
                    LoadedSlotsJson = table.Column<string>(type: "TEXT", nullable: true),
                    LoadSlotOperationAttemptId = table.Column<string>(type: "TEXT", nullable: false),
                    LoadCommandMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    UnloadSlotOperationAttemptId = table.Column<string>(type: "TEXT", nullable: false),
                    UnloadCommandMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    DispatchZone = table.Column<string>(type: "TEXT", nullable: false),
                    DispatchGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RemovedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RemovalReason = table.Column<string>(type: "TEXT", nullable: true),
                    DispatchZoneParameterVersion = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JourneyDemands", x => new { x.JourneyId, x.DemandId });
                });

            migrationBuilder.CreateTable(
                name: "JourneyStops",
                columns: table => new
                {
                    StopId = table.Column<string>(type: "TEXT", nullable: false),
                    JourneyId = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    StopRole = table.Column<string>(type: "TEXT", nullable: false),
                    StationId = table.Column<string>(type: "TEXT", nullable: false),
                    StationRiotId = table.Column<int>(type: "INTEGER", nullable: false),
                    DispatchZone = table.Column<string>(type: "TEXT", nullable: false),
                    OperationSessionId = table.Column<string>(type: "TEXT", nullable: false),
                    MovementLegId = table.Column<string>(type: "TEXT", nullable: false),
                    UpperId = table.Column<string>(type: "TEXT", nullable: false),
                    VehicleBusinessMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    WorklistMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    PlanMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    SublotRequestMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    DepartureSafetyCheckMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    DepartureSafetyCheckId = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JourneyStops", x => x.StopId);
                });

            migrationBuilder.CreateTable(
                name: "TransportDemandSuppressions",
                columns: table => new
                {
                    TransportDemandKey = table.Column<string>(type: "TEXT", nullable: false),
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    ReasonCode = table.Column<string>(type: "TEXT", nullable: false),
                    SuppressedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransportDemandSuppressions", x => x.TransportDemandKey);
                });

            migrationBuilder.CreateTable(
                name: "VehiclePurposeClaims",
                columns: table => new
                {
                    VehicleKey = table.Column<string>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", nullable: false),
                    JourneyId = table.Column<string>(type: "TEXT", nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehiclePurposeClaims", x => x.VehicleKey);
                });

            migrationBuilder.CreateTable(
                name: "VehicleSnapshotRevisions",
                columns: table => new
                {
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    VehicleBusinessRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    WorklistRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    PlanRevision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleSnapshotRevisions", x => x.AgvId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VehicleDispatchLeases_DemandId",
                table: "VehicleDispatchLeases",
                column: "DemandId");

            migrationBuilder.CreateIndex(
                name: "IX_JourneyRuntimes_DemandId",
                table: "JourneyRuntimes",
                column: "DemandId");

            migrationBuilder.CreateIndex(
                name: "IX_JourneyDemands_DemandId",
                table: "JourneyDemands",
                column: "DemandId",
                unique: true,
                filter: "RemovedAt IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_JourneyStops_JourneyId_Sequence",
                table: "JourneyStops",
                columns: new[] { "JourneyId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_TransportDemandSuppressions_DemandId",
                table: "TransportDemandSuppressions",
                column: "DemandId");

            migrationBuilder.CreateIndex(
                name: "IX_VehiclePurposeClaims_JourneyId",
                table: "VehiclePurposeClaims",
                column: "JourneyId");

            BackFill(migrationBuilder);
        }

        /// <summary>
        /// Every existing journey becomes one pickup stop, one unload stop and one demand, carrying its own ids unchanged;
        /// every active lease gets its purpose claim; every vehicle's counter starts at its highest stored revisions.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Runs with journeys in flight rather than refusing them (the MVP's 663f275a had to refuse because it changed the
        /// id derivation; this ticket does not). Nothing already stored is rewritten: the thirteen derived ids, the
        /// operation session and both legs are copied, so the peer and RIoT see exactly what they saw before.
        /// </para>
        /// <para>
        /// Statuses are read off the facts the journey already has, because the runtime does not maintain them in this
        /// ticket: the pickup stop is done once the gate order exists or the journey has moved past it, the unload stop is
        /// done when the demand succeeded and removed when the journey ended any other way, and the demand is loaded once
        /// its load committed or the journey is past the load. A blocked journey is placed by the same facts. A demand
        /// already Cancelled leaves no stop pending or active, whatever stage the journey was left in: a late recovery
        /// result can put an ended journey back to Blocked (OnboardRecoveryCoordinator), and a reader taking the first stop
        /// neither completed nor removed as the current one (control-server#208) must not find it still at its pickup.
        /// </para>
        /// </remarks>
        private static void BackFill(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO JourneyStops
                    (StopId, JourneyId, Sequence, StopRole, StationId, StationRiotId, DispatchZone, OperationSessionId,
                     MovementLegId, UpperId, VehicleBusinessMessageId, WorklistMessageId, PlanMessageId,
                     SublotRequestMessageId, DepartureSafetyCheckMessageId, DepartureSafetyCheckId, Status, CreatedAt)
                SELECT j.JourneyId || '|PICKUP', j.JourneyId, 1, 'PICKUP', j.PickupStationId, j.PickupStationRiotId,
                       j.DispatchZone, j.OperationSessionId, j.PickupMovementLegId, j.PickupUpperId,
                       j.VehicleBusinessMessageId, j.WorklistMessageId, j.PlanMessageId,
                       j.SublotRequestMessageId, j.PreDepartureSafetyCheckMessageId, j.PreDepartureSafetyCheckId,
                       CASE
                           WHEN j.Stage IN ('AwaitingGateArrival', 'AwaitingUnloadResult', 'Completed') THEN 'COMPLETED'
                           WHEN EXISTS (SELECT 1 FROM OrderIntents o WHERE o.MovementLegId = j.GateMovementLegId)
                               THEN 'COMPLETED'
                           WHEN d.Status = 'Cancelled' THEN 'REMOVED'
                           WHEN j.Stage = 'AwaitingPickupArrival' THEN 'PENDING'
                           ELSE 'ACTIVE'
                       END,
                       j.CreatedAt
                FROM JourneyRuntimes j
                LEFT JOIN AcceptedDemands d ON d.DemandId = j.DemandId;
                """);
            migrationBuilder.Sql(
                """
                INSERT INTO JourneyStops
                    (StopId, JourneyId, Sequence, StopRole, StationId, StationRiotId, DispatchZone, OperationSessionId,
                     MovementLegId, UpperId, VehicleBusinessMessageId, WorklistMessageId, PlanMessageId,
                     SublotRequestMessageId, DepartureSafetyCheckMessageId, DepartureSafetyCheckId, Status, CreatedAt)
                SELECT j.JourneyId || '|UNLOAD', j.JourneyId, 2, 'UNLOAD', j.GateStationId, j.GateStationRiotId,
                       j.DispatchZone, j.OperationSessionId, j.GateMovementLegId, j.GateUpperId,
                       j.GateVehicleBusinessMessageId, j.GateWorklistMessageId, j.GatePlanMessageId,
                       NULL, NULL, NULL,
                       CASE
                           WHEN d.Status = 'Succeeded' THEN 'COMPLETED'
                           WHEN j.Stage = 'Completed' OR d.Status = 'Cancelled' THEN 'REMOVED'
                           WHEN j.Stage = 'AwaitingUnloadResult' THEN 'ACTIVE'
                           WHEN EXISTS (SELECT 1 FROM StationOperations s
                                        WHERE s.SlotOperationAttemptId = j.UnloadSlotOperationAttemptId) THEN 'ACTIVE'
                           ELSE 'PENDING'
                       END,
                       j.CreatedAt
                FROM JourneyRuntimes j
                LEFT JOIN AcceptedDemands d ON d.DemandId = j.DemandId;
                """);
            migrationBuilder.Sql(
                """
                INSERT INTO JourneyDemands
                    (JourneyId, DemandId, PickupStopId, UnloadStopId, ExpectedBasketCount, TargetSlotsJson, LoadedSlotsJson,
                     LoadSlotOperationAttemptId, LoadCommandMessageId, UnloadSlotOperationAttemptId, UnloadCommandMessageId,
                     DispatchZone, DispatchGeneration, Status, AddedAt, RemovedAt, RemovalReason, DispatchZoneParameterVersion)
                SELECT j.JourneyId, j.DemandId, j.JourneyId || '|PICKUP', j.JourneyId || '|UNLOAD',
                       j.ExpectedBasketCount, j.TargetSlotsJson, NULL,
                       j.LoadSlotOperationAttemptId, j.LoadCommandMessageId, j.UnloadSlotOperationAttemptId, j.UnloadCommandMessageId,
                       j.DispatchZone, j.DispatchGeneration,
                       CASE
                           WHEN d.Status = 'Succeeded' THEN 'UNLOADED'
                           WHEN d.Status = 'Cancelled' OR j.Stage = 'Completed' THEN 'TERMINATED'
                           WHEN j.Stage IN ('AwaitingStationDeparture', 'AwaitingDepartureSafety',
                                            'AwaitingGateArrival', 'AwaitingUnloadResult') THEN 'LOADED'
                           WHEN EXISTS (SELECT 1 FROM StationOperations s
                                        WHERE s.SlotOperationAttemptId = j.LoadSlotOperationAttemptId
                                          AND s.Status = 'Committed') THEN 'LOADED'
                           ELSE 'PENDING_LOAD'
                       END,
                       j.CreatedAt, NULL, NULL, NULL
                FROM JourneyRuntimes j
                LEFT JOIN AcceptedDemands d ON d.DemandId = j.DemandId;
                """);
            migrationBuilder.Sql(
                """
                INSERT INTO VehiclePurposeClaims (VehicleKey, Purpose, JourneyId, ClaimedAt)
                SELECT VehicleKey, 'TRANSPORT', JourneyId, AcquiredAt
                FROM VehicleDispatchLeases
                WHERE ReleasedAt IS NULL;
                """);
            migrationBuilder.Sql(
                """
                INSERT INTO VehicleSnapshotRevisions (AgvId, VehicleBusinessRevision, WorklistRevision, PlanRevision)
                SELECT AgvId, MAX(VehicleBusinessRevision), MAX(WorklistRevision), MAX(PlanRevision)
                FROM JourneyRuntimes
                GROUP BY AgvId;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DispatchZoneParameters");

            migrationBuilder.DropTable(
                name: "DispatchZoneParameterVersions");

            migrationBuilder.DropTable(
                name: "JourneyDemands");

            migrationBuilder.DropTable(
                name: "JourneyStops");

            migrationBuilder.DropTable(
                name: "TransportDemandSuppressions");

            migrationBuilder.DropTable(
                name: "VehiclePurposeClaims");

            migrationBuilder.DropTable(
                name: "VehicleSnapshotRevisions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_VehicleDispatchLeases",
                table: "VehicleDispatchLeases");

            migrationBuilder.DropIndex(
                name: "IX_VehicleDispatchLeases_DemandId",
                table: "VehicleDispatchLeases");

            migrationBuilder.DropPrimaryKey(
                name: "PK_JourneyRuntimes",
                table: "JourneyRuntimes");

            migrationBuilder.DropIndex(
                name: "IX_JourneyRuntimes_DemandId",
                table: "JourneyRuntimes");

            migrationBuilder.DropColumn(
                name: "JourneyId",
                table: "VehicleDispatchLeases");

            migrationBuilder.DropColumn(
                name: "JourneyId",
                table: "JourneyRuntimes");

            migrationBuilder.DropColumn(
                name: "CargoHoldingStartedAt",
                table: "JourneyRuntimes");

            migrationBuilder.DropColumn(
                name: "FullSlotPositionsJson",
                table: "JourneyRuntimes");

            migrationBuilder.DropColumn(
                name: "LoadingClosedReason",
                table: "JourneyRuntimes");

            migrationBuilder.DropColumn(
                name: "LoadingPhaseState",
                table: "JourneyRuntimes");

            migrationBuilder.DropColumn(
                name: "YieldTriggeredAt",
                table: "JourneyRuntimes");

            migrationBuilder.DropColumn(
                name: "YieldTriggeredByVehicleKey",
                table: "JourneyRuntimes");

            migrationBuilder.DropColumn(
                name: "StarvationEscalatedAt",
                table: "JourneyBacklog");

            migrationBuilder.DropColumn(
                name: "StarvationEscalationParameterVersion",
                table: "JourneyBacklog");

            migrationBuilder.AddPrimaryKey(
                name: "PK_VehicleDispatchLeases",
                table: "VehicleDispatchLeases",
                column: "DemandId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_JourneyRuntimes",
                table: "JourneyRuntimes",
                column: "DemandId");
        }
    }
}
