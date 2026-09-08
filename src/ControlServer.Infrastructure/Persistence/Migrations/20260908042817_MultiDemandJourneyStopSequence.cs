using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Rebuilds the journey runtime around the journey's own identity: a stop sequence, and demands
    /// hanging off the stops they load at (ADR-cross-0057). Existing single-demand journeys migrate
    /// as the degenerate form the ADR describes -- one pickup stop, one gate stop, one demand -- and
    /// take their own DemandId as the journey identity, which is why nothing here has to invent one.
    /// </summary>
    /// <remarks>
    /// Written by hand. The scaffolded version dropped <c>DemandId</c> and renamed
    /// <c>WorklistMessageId</c> to <c>JourneyId</c>, which would have silently filled every journey
    /// identity with a message id.
    ///
    /// It refuses to run while any journey is still in flight, and that refusal is the point rather
    /// than caution: the deterministic message ids move with this change (a stop's worklist is now
    /// keyed on stop and round), so an in-flight journey would come out the other side unable to
    /// replay its own unacknowledged messages -- and the failure would be silent. This ships with
    /// protocol 0.2.0, a breaking release both ends install together, so there is a moment with no
    /// journey in flight by construction.
    /// </remarks>
    public partial class MultiDemandJourneyStopSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE "__W2GJourneyMigrationGuard" (
                    "Finding" TEXT NOT NULL
                        CONSTRAINT "SettleEveryJourneyBeforeMigratingToProtocol_0_2_0"
                        CHECK ("Finding" = 'NO_JOURNEY_IN_FLIGHT')
                );
                """);
            migrationBuilder.Sql("""
                INSERT INTO "__W2GJourneyMigrationGuard" ("Finding")
                SELECT CASE
                    WHEN EXISTS (SELECT 1 FROM "JourneyRuntimes" WHERE "Stage" <> 'Completed')
                    THEN 'JOURNEY_IN_FLIGHT'
                    ELSE 'NO_JOURNEY_IN_FLIGHT'
                END;
                """);
            migrationBuilder.Sql("""DROP TABLE "__W2GJourneyMigrationGuard";""");

            migrationBuilder.CreateTable(
                name: "JourneyStops",
                columns: table => new
                {
                    JourneyId = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    StationId = table.Column<string>(type: "TEXT", nullable: false),
                    StationRiotId = table.Column<int>(type: "INTEGER", nullable: false),
                    RouteEvidenceId = table.Column<string>(type: "TEXT", nullable: false),
                    MovementLegId = table.Column<string>(type: "TEXT", nullable: false),
                    UpperId = table.Column<string>(type: "TEXT", nullable: false),
                    LegType = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    VehicleBusinessRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    WorklistRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    PlanRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    VehicleBusinessMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    PlanMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    PreDepartureSafetyCheckMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    PreDepartureSafetyCheckId = table.Column<string>(type: "TEXT", nullable: false),
                    LoadRound = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsumedSafetyResultMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    SublotWaitStartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JourneyStops", x => new { x.JourneyId, x.Sequence });
                });

            migrationBuilder.CreateTable(
                name: "JourneyDemands",
                columns: table => new
                {
                    JourneyId = table.Column<string>(type: "TEXT", nullable: false),
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    StopSequence = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpectedBasketCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetSlotsJson = table.Column<string>(type: "TEXT", nullable: false),
                    LoadCommandMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    LoadSlotOperationAttemptId = table.Column<string>(type: "TEXT", nullable: false),
                    UnloadCommandMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    UnloadSlotOperationAttemptId = table.Column<string>(type: "TEXT", nullable: false),
                    ConsumedSublotMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    LoadCommandedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UnloadCommandedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LoadedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JourneyDemands", x => new { x.JourneyId, x.DemandId });
                });

            // The pickup stop of each existing journey, carrying that journey's route, leg and
            // published revisions unchanged. Every row here belongs to a journey the guard above
            // proved settled, so the stop is COMPLETED and the entry request round is the one it
            // ran.
            migrationBuilder.Sql("""
                INSERT INTO "JourneyStops" (
                    "JourneyId", "Sequence", "Role", "StationId", "StationRiotId", "RouteEvidenceId",
                    "MovementLegId", "UpperId", "LegType", "State",
                    "VehicleBusinessRevision", "WorklistRevision", "PlanRevision",
                    "VehicleBusinessMessageId", "PlanMessageId",
                    "PreDepartureSafetyCheckMessageId", "PreDepartureSafetyCheckId",
                    "LoadRound", "ConsumedSafetyResultMessageId", "SublotWaitStartedAt",
                    "CreatedAt", "UpdatedAt")
                SELECT
                    "DemandId", 1, 'PICKUP', "PickupStationId", "PickupStationRiotId", "RouteEvidenceId",
                    "PickupMovementLegId", "PickupUpperId", 'TO_PICKUP', 'COMPLETED',
                    "VehicleBusinessRevision", "WorklistRevision", "PlanRevision",
                    "VehicleBusinessMessageId", "PlanMessageId",
                    "PreDepartureSafetyCheckMessageId", "PreDepartureSafetyCheckId",
                    1, "ConsumedSafetyResultMessageId", "SublotWaitStartedAt",
                    "CreatedAt", "UpdatedAt"
                FROM "JourneyRuntimes";
                """);

            // The gate stop. Its revisions are one above the pickup stop's because that is what the
            // old engine published there. Its safety check ids are placeholders: the old model had
            // one check per journey -- the one that authorized leaving the pickup stop -- and the
            // gate never asked for one, so there is no real id to carry over. They are unique per
            // journey and no historical judgement reads them.
            migrationBuilder.Sql("""
                INSERT INTO "JourneyStops" (
                    "JourneyId", "Sequence", "Role", "StationId", "StationRiotId", "RouteEvidenceId",
                    "MovementLegId", "UpperId", "LegType", "State",
                    "VehicleBusinessRevision", "WorklistRevision", "PlanRevision",
                    "VehicleBusinessMessageId", "PlanMessageId",
                    "PreDepartureSafetyCheckMessageId", "PreDepartureSafetyCheckId",
                    "LoadRound", "ConsumedSafetyResultMessageId", "SublotWaitStartedAt",
                    "CreatedAt", "UpdatedAt")
                SELECT
                    "DemandId", 9, 'GATE', "GateStationId", "GateStationRiotId", "RouteEvidenceId",
                    "GateMovementLegId", "GateUpperId", 'TO_GATE', 'COMPLETED',
                    "VehicleBusinessRevision" + 1, "WorklistRevision" + 1, "PlanRevision" + 1,
                    "GateVehicleBusinessMessageId", "GatePlanMessageId",
                    'migrated-gate-safety-request-' || "DemandId",
                    'migrated-gate-safety-check-' || "DemandId",
                    1, NULL, NULL,
                    "CreatedAt", "UpdatedAt"
                FROM "JourneyRuntimes";
                """);

            // The one demand each journey carried, loaded at its pickup stop. Its terminal state
            // comes from the accepted demand rather than from the journey stage: a settled journey
            // either delivered its cargo or had it terminated, and the demand row is what recorded
            // which. Anything else is treated as terminated, so no row claims cargo came off the
            // vehicle without evidence that it did. Both commands went out at some point in a
            // settled journey; the exact instants were never recorded, so they take the journey's
            // creation time -- the fields only have to be non-null to say "commanded".
            migrationBuilder.Sql("""
                INSERT INTO "JourneyDemands" (
                    "JourneyId", "DemandId", "StopSequence", "ExpectedBasketCount", "TargetSlotsJson",
                    "LoadCommandMessageId", "LoadSlotOperationAttemptId",
                    "UnloadCommandMessageId", "UnloadSlotOperationAttemptId",
                    "ConsumedSublotMessageId", "LoadCommandedAt", "UnloadCommandedAt",
                    "State", "CreatedAt", "LoadedAt")
                SELECT
                    "r"."DemandId", "r"."DemandId", 1, "r"."ExpectedBasketCount", "r"."TargetSlotsJson",
                    "r"."LoadCommandMessageId", "r"."LoadSlotOperationAttemptId",
                    "r"."UnloadCommandMessageId", "r"."UnloadSlotOperationAttemptId",
                    "r"."ConsumedSublotMessageId", "r"."CreatedAt", "r"."CreatedAt",
                    CASE WHEN "d"."Status" = 'Succeeded' THEN 'Unloaded' ELSE 'Cancelled' END,
                    "r"."CreatedAt", NULL
                FROM "JourneyRuntimes" AS "r"
                LEFT JOIN "AcceptedDemands" AS "d" ON "d"."DemandId" = "r"."DemandId";
                """);

            // Rebuild JourneyRuntimes around JourneyId. The revision cursors move two above what the
            // journey published, which is the first value its successor may use: they are cursors
            // now, not per-journey counters, because a journey whose stop sequence grows would
            // outrun any fixed reservation.
            migrationBuilder.Sql("""
                CREATE TABLE "JourneyRuntimes_new" (
                    "JourneyId" TEXT NOT NULL CONSTRAINT "PK_JourneyRuntimes" PRIMARY KEY,
                    "Stage" TEXT NOT NULL,
                    "AgvId" TEXT NOT NULL,
                    "VehicleKey" TEXT NOT NULL,
                    "AgvLifecycleGeneration" INTEGER NOT NULL,
                    "MapId" INTEGER NOT NULL,
                    "MapIdentity" TEXT NOT NULL,
                    "DispatchZone" TEXT NOT NULL,
                    "GateStationId" TEXT NOT NULL,
                    "GateStationRiotId" INTEGER NOT NULL,
                    "OperationSessionId" TEXT NOT NULL,
                    "DispatchGeneration" INTEGER NOT NULL,
                    "CurrentStopSequence" INTEGER NOT NULL,
                    "NextStopSequence" INTEGER NULL,
                    "VehicleBusinessRevision" INTEGER NOT NULL,
                    "WorklistRevision" INTEGER NOT NULL,
                    "PlanRevision" INTEGER NOT NULL,
                    "HoldingStartedAt" TEXT NULL,
                    "LoadingClosedReason" TEXT NULL,
                    "BlockReasonCode" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                """);
            migrationBuilder.Sql("""
                INSERT INTO "JourneyRuntimes_new" (
                    "JourneyId", "Stage", "AgvId", "VehicleKey", "AgvLifecycleGeneration",
                    "MapId", "MapIdentity", "DispatchZone", "GateStationId", "GateStationRiotId",
                    "OperationSessionId", "DispatchGeneration", "CurrentStopSequence", "NextStopSequence",
                    "VehicleBusinessRevision", "WorklistRevision", "PlanRevision",
                    "HoldingStartedAt", "LoadingClosedReason", "BlockReasonCode",
                    "CreatedAt", "UpdatedAt")
                SELECT
                    "DemandId", "Stage", "AgvId", "VehicleKey", "AgvLifecycleGeneration",
                    "MapId", "MapIdentity", "DispatchZone", "GateStationId", "GateStationRiotId",
                    "OperationSessionId", "DispatchGeneration", 9, NULL,
                    "VehicleBusinessRevision" + 2, "WorklistRevision" + 2, "PlanRevision" + 2,
                    NULL, NULL, "BlockReasonCode",
                    "CreatedAt", "UpdatedAt"
                FROM "JourneyRuntimes";
                """);
            migrationBuilder.Sql("""DROP TABLE "JourneyRuntimes";""");
            migrationBuilder.Sql("""ALTER TABLE "JourneyRuntimes_new" RENAME TO "JourneyRuntimes";""");

            // The lease moves from the demand to the journey. No value changes: a migrated journey's
            // identity *is* its demand's, and a lease taken without a journey already stands under
            // the demand's own id, which is the same degenerate form.
            migrationBuilder.RenameColumn(
                name: "DemandId",
                table: "VehicleDispatchLeases",
                newName: "JourneyId");

            migrationBuilder.CreateIndex(
                name: "IX_JourneyStops_MovementLegId",
                table: "JourneyStops",
                column: "MovementLegId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JourneyStops_UpperId",
                table: "JourneyStops",
                column: "UpperId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JourneyDemands_DemandId",
                table: "JourneyDemands",
                column: "DemandId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JourneyDemands_LoadSlotOperationAttemptId",
                table: "JourneyDemands",
                column: "LoadSlotOperationAttemptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JourneyDemands_UnloadSlotOperationAttemptId",
                table: "JourneyDemands",
                column: "UnloadSlotOperationAttemptId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only the degenerate shape can go back: the old row has one column for each of the
            // pickup leg, the gate leg, the load command and the unload command, so a journey with
            // two pickup stops or two demands has nowhere to put the second. The guard refuses
            // rather than picking one and dropping the rest.
            migrationBuilder.Sql("""
                CREATE TABLE "__W2GJourneyRollbackGuard" (
                    "Finding" TEXT NOT NULL
                        CONSTRAINT "OnlySingleDemandJourneysCanRollBackToProtocol_0_1_1"
                        CHECK ("Finding" = 'EVERY_JOURNEY_IS_SINGLE_DEMAND')
                );
                """);
            migrationBuilder.Sql("""
                INSERT INTO "__W2GJourneyRollbackGuard" ("Finding")
                SELECT CASE WHEN EXISTS (
                    SELECT 1 FROM "JourneyDemands" GROUP BY "JourneyId" HAVING COUNT(*) > 1
                ) OR EXISTS (
                    SELECT 1 FROM "JourneyStops" WHERE "Role" = 'PICKUP'
                    GROUP BY "JourneyId" HAVING COUNT(*) > 1
                ) THEN 'MULTI_DEMAND_JOURNEY_PRESENT' ELSE 'EVERY_JOURNEY_IS_SINGLE_DEMAND' END;
                """);
            migrationBuilder.Sql("""DROP TABLE "__W2GJourneyRollbackGuard";""");

            migrationBuilder.RenameColumn(
                name: "JourneyId",
                table: "VehicleDispatchLeases",
                newName: "DemandId");

            migrationBuilder.Sql("""
                CREATE TABLE "JourneyRuntimes_old" (
                    "DemandId" TEXT NOT NULL CONSTRAINT "PK_JourneyRuntimes" PRIMARY KEY,
                    "Stage" TEXT NOT NULL,
                    "AgvId" TEXT NOT NULL,
                    "VehicleKey" TEXT NOT NULL,
                    "AgvLifecycleGeneration" INTEGER NOT NULL,
                    "MapId" INTEGER NOT NULL,
                    "MapIdentity" TEXT NOT NULL,
                    "DispatchZone" TEXT NOT NULL,
                    "RouteEvidenceId" TEXT NOT NULL,
                    "PickupStationId" TEXT NOT NULL,
                    "PickupStationRiotId" INTEGER NOT NULL,
                    "GateStationId" TEXT NOT NULL,
                    "GateStationRiotId" INTEGER NOT NULL,
                    "ExpectedBasketCount" INTEGER NOT NULL,
                    "TargetSlotsJson" TEXT NOT NULL,
                    "OperationSessionId" TEXT NOT NULL,
                    "PickupMovementLegId" TEXT NOT NULL,
                    "PickupUpperId" TEXT NOT NULL,
                    "GateMovementLegId" TEXT NOT NULL,
                    "GateUpperId" TEXT NOT NULL,
                    "DispatchGeneration" INTEGER NOT NULL,
                    "VehicleBusinessRevision" INTEGER NOT NULL,
                    "WorklistRevision" INTEGER NOT NULL,
                    "PlanRevision" INTEGER NOT NULL,
                    "VehicleBusinessMessageId" TEXT NOT NULL,
                    "WorklistMessageId" TEXT NOT NULL,
                    "PlanMessageId" TEXT NOT NULL,
                    "SublotRequestMessageId" TEXT NOT NULL,
                    "LoadCommandMessageId" TEXT NOT NULL,
                    "LoadSlotOperationAttemptId" TEXT NOT NULL,
                    "PreDepartureSafetyCheckMessageId" TEXT NOT NULL,
                    "PreDepartureSafetyCheckId" TEXT NOT NULL,
                    "GateVehicleBusinessMessageId" TEXT NOT NULL,
                    "GateWorklistMessageId" TEXT NOT NULL,
                    "GatePlanMessageId" TEXT NOT NULL,
                    "UnloadCommandMessageId" TEXT NOT NULL,
                    "UnloadSlotOperationAttemptId" TEXT NOT NULL,
                    "ConsumedSublotMessageId" TEXT NULL,
                    "ConsumedSafetyResultMessageId" TEXT NULL,
                    "SublotWaitStartedAt" TEXT NULL,
                    "BlockReasonCode" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                """);
            // The per-round worklist and entry-request ids are not stored any more, so the old
            // columns take placeholders derived from the journey id.
            migrationBuilder.Sql("""
                INSERT INTO "JourneyRuntimes_old"
                SELECT
                    "d"."DemandId", "r"."Stage", "r"."AgvId", "r"."VehicleKey", "r"."AgvLifecycleGeneration",
                    "r"."MapId", "r"."MapIdentity", "r"."DispatchZone", "p"."RouteEvidenceId",
                    "p"."StationId", "p"."StationRiotId", "r"."GateStationId", "r"."GateStationRiotId",
                    "d"."ExpectedBasketCount", "d"."TargetSlotsJson", "r"."OperationSessionId",
                    "p"."MovementLegId", "p"."UpperId", "g"."MovementLegId", "g"."UpperId",
                    "r"."DispatchGeneration",
                    "p"."VehicleBusinessRevision", "p"."WorklistRevision", "p"."PlanRevision",
                    "p"."VehicleBusinessMessageId",
                    'migrated-worklist-' || "r"."JourneyId",
                    "p"."PlanMessageId",
                    'migrated-sublot-request-' || "r"."JourneyId",
                    "d"."LoadCommandMessageId", "d"."LoadSlotOperationAttemptId",
                    "p"."PreDepartureSafetyCheckMessageId", "p"."PreDepartureSafetyCheckId",
                    "g"."VehicleBusinessMessageId",
                    'migrated-gate-worklist-' || "r"."JourneyId",
                    "g"."PlanMessageId",
                    "d"."UnloadCommandMessageId", "d"."UnloadSlotOperationAttemptId",
                    "d"."ConsumedSublotMessageId", "p"."ConsumedSafetyResultMessageId",
                    "p"."SublotWaitStartedAt", "r"."BlockReasonCode",
                    "r"."CreatedAt", "r"."UpdatedAt"
                FROM "JourneyRuntimes" AS "r"
                JOIN "JourneyDemands" AS "d" ON "d"."JourneyId" = "r"."JourneyId"
                JOIN "JourneyStops" AS "p" ON "p"."JourneyId" = "r"."JourneyId" AND "p"."Role" = 'PICKUP'
                JOIN "JourneyStops" AS "g" ON "g"."JourneyId" = "r"."JourneyId" AND "g"."Role" = 'GATE';
                """);
            migrationBuilder.Sql("""DROP TABLE "JourneyRuntimes";""");
            migrationBuilder.Sql("""ALTER TABLE "JourneyRuntimes_old" RENAME TO "JourneyRuntimes";""");

            migrationBuilder.DropTable(
                name: "JourneyDemands");

            migrationBuilder.DropTable(
                name: "JourneyStops");
        }
    }
}
