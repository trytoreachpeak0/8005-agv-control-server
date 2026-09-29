using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // EF migration generator emits inline metadata arrays.

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Batch8VehiclePurposePersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StationExclusivities",
                columns: table => new
                {
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationKind = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    VehicleKey = table.Column<string>(type: "TEXT", nullable: false),
                    JourneyId = table.Column<string>(type: "TEXT", nullable: false),
                    StateSince = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    WaitingPointVersion = table.Column<long>(type: "INTEGER", nullable: true),
                    RecordId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StationExclusivities", x => new { x.MapId, x.StationId });
                    table.CheckConstraint("CK_StationExclusivities_State", "\"State\" IN ('RESERVED', 'OCCUPIED')");
                    table.CheckConstraint("CK_StationExclusivities_StationKind", "\"StationKind\" IN ('WAITING_POINT', 'FIXED_TASK_STATION')");
                });

            migrationBuilder.CreateTable(
                name: "StationExclusivityRecords",
                columns: table => new
                {
                    RecordId = table.Column<string>(type: "TEXT", nullable: false),
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationKind = table.Column<string>(type: "TEXT", nullable: false),
                    VehicleKey = table.Column<string>(type: "TEXT", nullable: false),
                    JourneyId = table.Column<string>(type: "TEXT", nullable: false),
                    WaitingPointVersion = table.Column<long>(type: "INTEGER", nullable: true),
                    ReservedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    OccupiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ReleaseReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StationExclusivityRecords", x => x.RecordId);
                    table.CheckConstraint("CK_StationExclusivityRecords_StationKind", "\"StationKind\" IN ('WAITING_POINT', 'FIXED_TASK_STATION')");
                });

            migrationBuilder.CreateTable(
                name: "VehiclePurposeClaimRecords",
                columns: table => new
                {
                    RecordId = table.Column<string>(type: "TEXT", nullable: false),
                    VehicleKey = table.Column<string>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", nullable: false),
                    JourneyId = table.Column<string>(type: "TEXT", nullable: false),
                    AcquiredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ReleaseReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehiclePurposeClaimRecords", x => x.RecordId);
                    table.CheckConstraint("CK_VehiclePurposeClaimRecords_Purpose", "\"Purpose\" IN ('TRANSPORT', 'CHARGING', 'CLEARING_MAINTENANCE', 'IDLE_RETURN')");
                });

            migrationBuilder.CreateTable(
                name: "WaitingPoints",
                columns: table => new
                {
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationName = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaitingPoints", x => new { x.Version, x.MapId, x.StationId });
                });

            migrationBuilder.CreateTable(
                name: "WaitingPointVehicleScopes",
                columns: table => new
                {
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationId = table.Column<int>(type: "INTEGER", nullable: false),
                    VehicleKey = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaitingPointVehicleScopes", x => new { x.Version, x.MapId, x.StationId, x.VehicleKey });
                });

            migrationBuilder.CreateTable(
                name: "WaitingPointVersions",
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
                    table.PrimaryKey("PK_WaitingPointVersions", x => x.Version);
                });

            // Not AddCheckConstraint: on SQLite that rebuilds the table with its columns re-ordered (key first, the rest
            // alphabetically), and every reader that dumps a table column by column would see a different table. The rebuild
            // below is the same one EF performs, with the columns where they were.
            OrderPreservingRebuild.Apply(migrationBuilder, VehiclePurposeClaimsWithPurposeCheck);

            // Choice A of control-server#386: an idle return is a journey without a demand. These four tables take nulls in
            // the anchor demand and the transport-only columns; every other column, index and row stays as it was.
            OrderPreservingRebuild.Apply(migrationBuilder, JourneyRuntimesWithIdleReturnNullable);
            OrderPreservingRebuild.Apply(migrationBuilder, OrderIntentsWithIdleReturnNullable);
            OrderPreservingRebuild.Apply(migrationBuilder, RiotDispatchAuditEventsWithIdleReturnNullable);
            OrderPreservingRebuild.Apply(migrationBuilder, ExperimentalRiotCreateAuthorizationsWithIdleReturnNullable);

            migrationBuilder.CreateIndex(
                name: "IX_StationExclusivities_JourneyId",
                table: "StationExclusivities",
                column: "JourneyId");

            migrationBuilder.CreateIndex(
                name: "IX_StationExclusivities_RecordId",
                table: "StationExclusivities",
                column: "RecordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StationExclusivities_VehicleKey",
                table: "StationExclusivities",
                column: "VehicleKey");

            migrationBuilder.CreateIndex(
                name: "IX_StationExclusivityRecords_JourneyId",
                table: "StationExclusivityRecords",
                column: "JourneyId");

            migrationBuilder.CreateIndex(
                name: "IX_StationExclusivityRecords_MapId_StationId",
                table: "StationExclusivityRecords",
                columns: new[] { "MapId", "StationId" },
                unique: true,
                filter: "ReleasedAt IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StationExclusivityRecords_VehicleKey",
                table: "StationExclusivityRecords",
                column: "VehicleKey");

            migrationBuilder.CreateIndex(
                name: "IX_VehiclePurposeClaimRecords_JourneyId",
                table: "VehiclePurposeClaimRecords",
                column: "JourneyId");

            migrationBuilder.CreateIndex(
                name: "IX_VehiclePurposeClaimRecords_VehicleKey",
                table: "VehiclePurposeClaimRecords",
                column: "VehicleKey",
                unique: true,
                filter: "ReleasedAt IS NULL");

            // control-server#386: every claim held at the moment of the upgrade gets its "acquired" record, so the history
            // starts complete. The id is derived, not random: a claim's journey holds at most one claim, and migrating down
            // and up again writes the same rows. Journeys in flight are expected here; nothing else is read or changed.
            migrationBuilder.Sql(
                "INSERT INTO VehiclePurposeClaimRecords (RecordId, VehicleKey, Purpose, JourneyId, AcquiredAt) " +
                "SELECT 'backfill|' || JourneyId, VehicleKey, Purpose, JourneyId, ClaimedAt FROM VehiclePurposeClaims;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StationExclusivities");

            migrationBuilder.DropTable(
                name: "StationExclusivityRecords");

            migrationBuilder.DropTable(
                name: "VehiclePurposeClaimRecords");

            migrationBuilder.DropTable(
                name: "WaitingPoints");

            migrationBuilder.DropTable(
                name: "WaitingPointVehicleScopes");

            migrationBuilder.DropTable(
                name: "WaitingPointVersions");

            OrderPreservingRebuild.Apply(migrationBuilder, VehiclePurposeClaimsAtMapNameBaselines);
            OrderPreservingRebuild.Apply(migrationBuilder, JourneyRuntimesAtMapNameBaselines);
            OrderPreservingRebuild.Apply(migrationBuilder, OrderIntentsAtMapNameBaselines);
            OrderPreservingRebuild.Apply(migrationBuilder, RiotDispatchAuditEventsAtMapNameBaselines);
            OrderPreservingRebuild.Apply(migrationBuilder, ExperimentalRiotCreateAuthorizationsAtMapNameBaselines);
        }

        // Table definitions exactly as SQLite stored them at 20260928153736_MapNameBaselines, and as this migration leaves
        // them. Each is written under a temporary name that the final rename replaces, so the stored text of the renamed
        // table is this text with the real name.

        private static readonly OrderPreservingRebuild.Table VehiclePurposeClaimsAtMapNameBaselines = new(
            "VehiclePurposeClaims",
            """
            CREATE TABLE "ef_temp_VehiclePurposeClaims" (
                "VehicleKey" TEXT NOT NULL CONSTRAINT "PK_VehiclePurposeClaims" PRIMARY KEY,
                "Purpose" TEXT NOT NULL,
                "JourneyId" TEXT NOT NULL,
                "ClaimedAt" TEXT NOT NULL
            )
            """,
            ["VehicleKey", "Purpose", "JourneyId", "ClaimedAt"],
            ["""CREATE INDEX "IX_VehiclePurposeClaims_JourneyId" ON "VehiclePurposeClaims" ("JourneyId")"""]);

        private static readonly OrderPreservingRebuild.Table VehiclePurposeClaimsWithPurposeCheck =
            VehiclePurposeClaimsAtMapNameBaselines with
            {
                CreateSql = """
                    CREATE TABLE "ef_temp_VehiclePurposeClaims" (
                        "VehicleKey" TEXT NOT NULL CONSTRAINT "PK_VehiclePurposeClaims" PRIMARY KEY,
                        "Purpose" TEXT NOT NULL,
                        "JourneyId" TEXT NOT NULL,
                        "ClaimedAt" TEXT NOT NULL,
                        CONSTRAINT "CK_VehiclePurposeClaims_Purpose" CHECK ("Purpose" IN ('TRANSPORT', 'CHARGING', 'CLEARING_MAINTENANCE', 'IDLE_RETURN'))
                    )
                    """
            };

        private static readonly OrderPreservingRebuild.Table JourneyRuntimesAtMapNameBaselines = new(
            "JourneyRuntimes",
            """
            CREATE TABLE "ef_temp_JourneyRuntimes" (
                "JourneyId" TEXT NOT NULL CONSTRAINT "PK_JourneyRuntimes" PRIMARY KEY,
                "AgvId" TEXT NOT NULL,
                "AgvLifecycleGeneration" INTEGER NOT NULL,
                "BlockReasonCode" TEXT NULL,
                "BlockReasonSince" TEXT NULL,
                "CargoHoldingStartedAt" TEXT NULL,
                "ConsumedSafetyResultMessageId" TEXT NULL,
                "ConsumedSublotMessageId" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "DemandId" TEXT NOT NULL,
                "DispatchGeneration" INTEGER NOT NULL,
                "DispatchZone" TEXT NOT NULL,
                "ExpectedBasketCount" INTEGER NOT NULL,
                "FullSlotPositionsJson" TEXT NULL,
                "GateMovementLegId" TEXT NOT NULL,
                "GatePlanMessageId" TEXT NOT NULL,
                "GateStationId" TEXT NOT NULL,
                "GateStationRiotId" INTEGER NOT NULL,
                "GateUpperId" TEXT NOT NULL,
                "GateVehicleBusinessMessageId" TEXT NOT NULL,
                "GateWorklistMessageId" TEXT NOT NULL,
                "LoadCommandMessageId" TEXT NOT NULL,
                "LoadSlotOperationAttemptId" TEXT NOT NULL,
                "LoadingClosedReason" TEXT NULL,
                "LoadingPhaseState" TEXT NULL,
                "MapId" INTEGER NOT NULL,
                "MapIdentity" TEXT NOT NULL,
                "OperationSessionId" TEXT NOT NULL,
                "PickupMovementLegId" TEXT NOT NULL,
                "PickupStationId" TEXT NOT NULL,
                "PickupStationRiotId" INTEGER NOT NULL,
                "PickupUpperId" TEXT NOT NULL,
                "PlanMessageId" TEXT NOT NULL,
                "PlanRevision" INTEGER NOT NULL,
                "PreDepartureSafetyCheckId" TEXT NOT NULL,
                "PreDepartureSafetyCheckMessageId" TEXT NOT NULL,
                "RouteEvidenceId" TEXT NOT NULL,
                "Stage" TEXT NOT NULL,
                "StationDepartureWaitStartedAt" TEXT NULL,
                "SublotRequestMessageId" TEXT NOT NULL,
                "TargetSlotsJson" TEXT NOT NULL,
                "UnloadCommandMessageId" TEXT NOT NULL,
                "UnloadSlotOperationAttemptId" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "VehicleBusinessMessageId" TEXT NOT NULL,
                "VehicleBusinessRevision" INTEGER NOT NULL,
                "VehicleKey" TEXT NOT NULL,
                "WorklistMessageId" TEXT NOT NULL,
                "WorklistRevision" INTEGER NOT NULL,
                "YieldTriggeredAt" TEXT NULL,
                "YieldTriggeredByVehicleKey" TEXT NULL
            , "AreaEndAdmissionRevokedSince" TEXT NULL, "WaitingBatteryObservedAt" TEXT NULL, "WaitingBatteryPercent" INTEGER NULL, "WaitingSince" TEXT NULL, "WaitingWarnedAt" TEXT NULL, "Version" INTEGER NOT NULL DEFAULT 0)
            """,
            ["JourneyId", "AgvId", "AgvLifecycleGeneration", "BlockReasonCode", "BlockReasonSince", "CargoHoldingStartedAt", "ConsumedSafetyResultMessageId", "ConsumedSublotMessageId", "CreatedAt", "DemandId", "DispatchGeneration", "DispatchZone", "ExpectedBasketCount", "FullSlotPositionsJson", "GateMovementLegId", "GatePlanMessageId", "GateStationId", "GateStationRiotId", "GateUpperId", "GateVehicleBusinessMessageId", "GateWorklistMessageId", "LoadCommandMessageId", "LoadSlotOperationAttemptId", "LoadingClosedReason", "LoadingPhaseState", "MapId", "MapIdentity", "OperationSessionId", "PickupMovementLegId", "PickupStationId", "PickupStationRiotId", "PickupUpperId", "PlanMessageId", "PlanRevision", "PreDepartureSafetyCheckId", "PreDepartureSafetyCheckMessageId", "RouteEvidenceId", "Stage", "StationDepartureWaitStartedAt", "SublotRequestMessageId", "TargetSlotsJson", "UnloadCommandMessageId", "UnloadSlotOperationAttemptId", "UpdatedAt", "VehicleBusinessMessageId", "VehicleBusinessRevision", "VehicleKey", "WorklistMessageId", "WorklistRevision", "YieldTriggeredAt", "YieldTriggeredByVehicleKey", "AreaEndAdmissionRevokedSince", "WaitingBatteryObservedAt", "WaitingBatteryPercent", "WaitingSince", "WaitingWarnedAt", "Version"],
            [
                """CREATE INDEX "IX_JourneyRuntimes_DemandId" ON "JourneyRuntimes" ("DemandId")""",
            ]);

        private static readonly OrderPreservingRebuild.Table JourneyRuntimesWithIdleReturnNullable =
            JourneyRuntimesAtMapNameBaselines with
            {
                CreateSql = """
                CREATE TABLE "ef_temp_JourneyRuntimes" (
                    "JourneyId" TEXT NOT NULL CONSTRAINT "PK_JourneyRuntimes" PRIMARY KEY,
                    "AgvId" TEXT NOT NULL,
                    "AgvLifecycleGeneration" INTEGER NOT NULL,
                    "BlockReasonCode" TEXT NULL,
                    "BlockReasonSince" TEXT NULL,
                    "CargoHoldingStartedAt" TEXT NULL,
                    "ConsumedSafetyResultMessageId" TEXT NULL,
                    "ConsumedSublotMessageId" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "DemandId" TEXT NULL,
                    "DispatchGeneration" INTEGER NOT NULL,
                    "DispatchZone" TEXT NOT NULL,
                    "ExpectedBasketCount" INTEGER NOT NULL,
                    "FullSlotPositionsJson" TEXT NULL,
                    "GateMovementLegId" TEXT NULL,
                    "GatePlanMessageId" TEXT NULL,
                    "GateStationId" TEXT NULL,
                    "GateStationRiotId" INTEGER NOT NULL,
                    "GateUpperId" TEXT NULL,
                    "GateVehicleBusinessMessageId" TEXT NULL,
                    "GateWorklistMessageId" TEXT NULL,
                    "LoadCommandMessageId" TEXT NULL,
                    "LoadSlotOperationAttemptId" TEXT NULL,
                    "LoadingClosedReason" TEXT NULL,
                    "LoadingPhaseState" TEXT NULL,
                    "MapId" INTEGER NOT NULL,
                    "MapIdentity" TEXT NOT NULL,
                    "OperationSessionId" TEXT NOT NULL,
                    "PickupMovementLegId" TEXT NOT NULL,
                    "PickupStationId" TEXT NOT NULL,
                    "PickupStationRiotId" INTEGER NOT NULL,
                    "PickupUpperId" TEXT NOT NULL,
                    "PlanMessageId" TEXT NOT NULL,
                    "PlanRevision" INTEGER NOT NULL,
                    "PreDepartureSafetyCheckId" TEXT NULL,
                    "PreDepartureSafetyCheckMessageId" TEXT NULL,
                    "RouteEvidenceId" TEXT NOT NULL,
                    "Stage" TEXT NOT NULL,
                    "StationDepartureWaitStartedAt" TEXT NULL,
                    "SublotRequestMessageId" TEXT NULL,
                    "TargetSlotsJson" TEXT NULL,
                    "UnloadCommandMessageId" TEXT NULL,
                    "UnloadSlotOperationAttemptId" TEXT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    "VehicleBusinessMessageId" TEXT NOT NULL,
                    "VehicleBusinessRevision" INTEGER NOT NULL,
                    "VehicleKey" TEXT NOT NULL,
                    "WorklistMessageId" TEXT NOT NULL,
                    "WorklistRevision" INTEGER NOT NULL,
                    "YieldTriggeredAt" TEXT NULL,
                    "YieldTriggeredByVehicleKey" TEXT NULL
                , "AreaEndAdmissionRevokedSince" TEXT NULL, "WaitingBatteryObservedAt" TEXT NULL, "WaitingBatteryPercent" INTEGER NULL, "WaitingSince" TEXT NULL, "WaitingWarnedAt" TEXT NULL, "Version" INTEGER NOT NULL DEFAULT 0)
                """
            };

        private static readonly OrderPreservingRebuild.Table OrderIntentsAtMapNameBaselines = new(
            "OrderIntents",
            """
            CREATE TABLE "ef_temp_OrderIntents" (
                "MovementLegId" TEXT NOT NULL CONSTRAINT "PK_OrderIntents" PRIMARY KEY,
                "DemandId" TEXT NOT NULL,
                "UpperId" TEXT NOT NULL,
                "Purpose" TEXT NOT NULL,
                "TargetStationId" TEXT NOT NULL,
                "VehicleKey" TEXT NOT NULL,
                "MapId" INTEGER NOT NULL,
                "DestinationStationId" INTEGER NOT NULL,
                "AgvLifecycleGeneration" INTEGER NOT NULL,
                "DispatchGeneration" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "OrderId" TEXT NULL
            , "CreateAttemptCount" INTEGER NULL, "CreateAttemptId" TEXT NULL, "CreateDispatchArmedAt" TEXT NULL, "DispatchAuditVersion" INTEGER NULL, "LastCreateOutcome" TEXT NULL, "LastCreateOutcomeAt" TEXT NULL, "LastCreateReceiptJson" TEXT NULL, "LastReconciliationOutcome" TEXT NULL, "LastReconciliationOutcomeAt" TEXT NULL, "LastReconciliationReceiptJson" TEXT NULL, "DispatchAuditSequence" INTEGER NULL, "ExperimentalCreateAuthorizationId" TEXT NULL, "VehicleOccupancyClaimedAt" TEXT NULL, "VehicleOccupancyReleasedAt" TEXT NULL)
            """,
            ["MovementLegId", "DemandId", "UpperId", "Purpose", "TargetStationId", "VehicleKey", "MapId", "DestinationStationId", "AgvLifecycleGeneration", "DispatchGeneration", "CreatedAt", "Status", "OrderId", "CreateAttemptCount", "CreateAttemptId", "CreateDispatchArmedAt", "DispatchAuditVersion", "LastCreateOutcome", "LastCreateOutcomeAt", "LastCreateReceiptJson", "LastReconciliationOutcome", "LastReconciliationOutcomeAt", "LastReconciliationReceiptJson", "DispatchAuditSequence", "ExperimentalCreateAuthorizationId", "VehicleOccupancyClaimedAt", "VehicleOccupancyReleasedAt"],
            [
                """CREATE UNIQUE INDEX "IX_OrderIntents_UpperId" ON "OrderIntents" ("UpperId")""",
                """CREATE UNIQUE INDEX "IX_OrderIntents_CreateAttemptId" ON "OrderIntents" ("CreateAttemptId") WHERE CreateAttemptId IS NOT NULL""",
                """CREATE UNIQUE INDEX "IX_OrderIntents_ExperimentalCreateAuthorizationId" ON "OrderIntents" ("ExperimentalCreateAuthorizationId") WHERE ExperimentalCreateAuthorizationId IS NOT NULL""",
                """CREATE UNIQUE INDEX "IX_OrderIntents_VehicleKey" ON "OrderIntents" ("VehicleKey") WHERE VehicleOccupancyClaimedAt IS NOT NULL AND VehicleOccupancyReleasedAt IS NULL""",
            ]);

        private static readonly OrderPreservingRebuild.Table OrderIntentsWithIdleReturnNullable =
            OrderIntentsAtMapNameBaselines with
            {
                CreateSql = """
                CREATE TABLE "ef_temp_OrderIntents" (
                    "MovementLegId" TEXT NOT NULL CONSTRAINT "PK_OrderIntents" PRIMARY KEY,
                    "DemandId" TEXT NULL,
                    "UpperId" TEXT NOT NULL,
                    "Purpose" TEXT NOT NULL,
                    "TargetStationId" TEXT NOT NULL,
                    "VehicleKey" TEXT NOT NULL,
                    "MapId" INTEGER NOT NULL,
                    "DestinationStationId" INTEGER NOT NULL,
                    "AgvLifecycleGeneration" INTEGER NOT NULL,
                    "DispatchGeneration" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "Status" TEXT NOT NULL,
                    "OrderId" TEXT NULL
                , "CreateAttemptCount" INTEGER NULL, "CreateAttemptId" TEXT NULL, "CreateDispatchArmedAt" TEXT NULL, "DispatchAuditVersion" INTEGER NULL, "LastCreateOutcome" TEXT NULL, "LastCreateOutcomeAt" TEXT NULL, "LastCreateReceiptJson" TEXT NULL, "LastReconciliationOutcome" TEXT NULL, "LastReconciliationOutcomeAt" TEXT NULL, "LastReconciliationReceiptJson" TEXT NULL, "DispatchAuditSequence" INTEGER NULL, "ExperimentalCreateAuthorizationId" TEXT NULL, "VehicleOccupancyClaimedAt" TEXT NULL, "VehicleOccupancyReleasedAt" TEXT NULL)
                """
            };

        private static readonly OrderPreservingRebuild.Table RiotDispatchAuditEventsAtMapNameBaselines = new(
            "RiotDispatchAuditEvents",
            """
            CREATE TABLE "ef_temp_RiotDispatchAuditEvents" (
                "AuditEventId" TEXT NOT NULL CONSTRAINT "PK_RiotDispatchAuditEvents" PRIMARY KEY,
                "MovementLegId" TEXT NOT NULL,
                "DemandId" TEXT NOT NULL,
                "UpperId" TEXT NOT NULL,
                "DispatchGeneration" INTEGER NOT NULL,
                "Sequence" INTEGER NOT NULL,
                "AttemptId" TEXT NULL,
                "AttemptNumber" INTEGER NULL,
                "Phase" TEXT NOT NULL,
                "Outcome" TEXT NOT NULL,
                "OccurredAt" TEXT NOT NULL,
                "RequestSemanticSha256" TEXT NULL,
                "ReceiptOperation" TEXT NULL,
                "ReceiptClassification" TEXT NULL,
                "ReceiptObservedAt" TEXT NULL,
                "HttpStatusCode" INTEGER NULL,
                "BusinessCode" TEXT NULL,
                "ResultPresent" INTEGER NULL,
                "ReturnedOrderId" TEXT NULL,
                "FailureCategory" TEXT NULL
            , "EligibilityBasis" TEXT NULL, "ExperimentalAuthorizationId" TEXT NULL)
            """,
            ["AuditEventId", "MovementLegId", "DemandId", "UpperId", "DispatchGeneration", "Sequence", "AttemptId", "AttemptNumber", "Phase", "Outcome", "OccurredAt", "RequestSemanticSha256", "ReceiptOperation", "ReceiptClassification", "ReceiptObservedAt", "HttpStatusCode", "BusinessCode", "ResultPresent", "ReturnedOrderId", "FailureCategory", "EligibilityBasis", "ExperimentalAuthorizationId"],
            [
                """CREATE INDEX "IX_RiotDispatchAuditEvents_AttemptId" ON "RiotDispatchAuditEvents" ("AttemptId")""",
                """CREATE UNIQUE INDEX "IX_RiotDispatchAuditEvents_MovementLegId_Sequence" ON "RiotDispatchAuditEvents" ("MovementLegId", "Sequence")""",
                """CREATE INDEX "IX_RiotDispatchAuditEvents_ExperimentalAuthorizationId" ON "RiotDispatchAuditEvents" ("ExperimentalAuthorizationId")""",
            ]);

        private static readonly OrderPreservingRebuild.Table RiotDispatchAuditEventsWithIdleReturnNullable =
            RiotDispatchAuditEventsAtMapNameBaselines with
            {
                CreateSql = """
                CREATE TABLE "ef_temp_RiotDispatchAuditEvents" (
                    "AuditEventId" TEXT NOT NULL CONSTRAINT "PK_RiotDispatchAuditEvents" PRIMARY KEY,
                    "MovementLegId" TEXT NOT NULL,
                    "DemandId" TEXT NULL,
                    "UpperId" TEXT NOT NULL,
                    "DispatchGeneration" INTEGER NOT NULL,
                    "Sequence" INTEGER NOT NULL,
                    "AttemptId" TEXT NULL,
                    "AttemptNumber" INTEGER NULL,
                    "Phase" TEXT NOT NULL,
                    "Outcome" TEXT NOT NULL,
                    "OccurredAt" TEXT NOT NULL,
                    "RequestSemanticSha256" TEXT NULL,
                    "ReceiptOperation" TEXT NULL,
                    "ReceiptClassification" TEXT NULL,
                    "ReceiptObservedAt" TEXT NULL,
                    "HttpStatusCode" INTEGER NULL,
                    "BusinessCode" TEXT NULL,
                    "ResultPresent" INTEGER NULL,
                    "ReturnedOrderId" TEXT NULL,
                    "FailureCategory" TEXT NULL
                , "EligibilityBasis" TEXT NULL, "ExperimentalAuthorizationId" TEXT NULL)
                """
            };

        private static readonly OrderPreservingRebuild.Table ExperimentalRiotCreateAuthorizationsAtMapNameBaselines = new(
            "ExperimentalRiotCreateAuthorizations",
            """
            CREATE TABLE "ef_temp_ExperimentalRiotCreateAuthorizations" (
                "AuthorizationId" TEXT NOT NULL CONSTRAINT "PK_ExperimentalRiotCreateAuthorizations" PRIMARY KEY,
                "AuthorizationVersion" INTEGER NOT NULL,
                "UpperId" TEXT NOT NULL,
                "DemandId" TEXT NOT NULL,
                "MovementLegId" TEXT NOT NULL,
                "AgvLifecycleGeneration" INTEGER NOT NULL,
                "DispatchGeneration" INTEGER NOT NULL,
                "ExpiresAt" TEXT NOT NULL,
                "PersistedAt" TEXT NOT NULL,
                "ConsumedAt" TEXT NULL,
                "ConsumedByAttemptId" TEXT NULL
            )
            """,
            ["AuthorizationId", "AuthorizationVersion", "UpperId", "DemandId", "MovementLegId", "AgvLifecycleGeneration", "DispatchGeneration", "ExpiresAt", "PersistedAt", "ConsumedAt", "ConsumedByAttemptId"],
            [
                """CREATE UNIQUE INDEX "IX_ExperimentalRiotCreateAuthorizations_ConsumedByAttemptId" ON "ExperimentalRiotCreateAuthorizations" ("ConsumedByAttemptId") WHERE ConsumedByAttemptId IS NOT NULL""",
                """CREATE UNIQUE INDEX "IX_ExperimentalRiotCreateAuthorizations_UpperId" ON "ExperimentalRiotCreateAuthorizations" ("UpperId")""",
            ]);

        private static readonly OrderPreservingRebuild.Table ExperimentalRiotCreateAuthorizationsWithIdleReturnNullable =
            ExperimentalRiotCreateAuthorizationsAtMapNameBaselines with
            {
                CreateSql = """
                CREATE TABLE "ef_temp_ExperimentalRiotCreateAuthorizations" (
                    "AuthorizationId" TEXT NOT NULL CONSTRAINT "PK_ExperimentalRiotCreateAuthorizations" PRIMARY KEY,
                    "AuthorizationVersion" INTEGER NOT NULL,
                    "UpperId" TEXT NOT NULL,
                    "DemandId" TEXT NULL,
                    "MovementLegId" TEXT NOT NULL,
                    "AgvLifecycleGeneration" INTEGER NOT NULL,
                    "DispatchGeneration" INTEGER NOT NULL,
                    "ExpiresAt" TEXT NOT NULL,
                    "PersistedAt" TEXT NOT NULL,
                    "ConsumedAt" TEXT NULL,
                    "ConsumedByAttemptId" TEXT NULL
                )
                """
            };

        /// <summary>
        /// The table rebuild EF performs for a change SQLite cannot ALTER, keeping the columns in the order they had.
        /// </summary>
        /// <remarks>
        /// Written by hand because EF's own rebuild re-orders the columns. No table in this schema has a foreign key, so none of EF's <c>PRAGMA foreign_keys</c> handling is needed, and the
        /// four statements run inside the migration's transaction: a row the new definition refuses fails the migration whole.
        /// </remarks>
        private static class OrderPreservingRebuild
        {
            internal sealed record Table(string Name, string CreateSql, string[] Columns, string[] IndexSql);

            internal static void Apply(MigrationBuilder migrationBuilder, Table table)
            {
                string temporary = "ef_temp_" + table.Name;
                string columns = string.Join(", ", table.Columns.Select(column => $"\"{column}\""));
                // EF writes its own CREATE TABLE with the running machine's line ending, and SQLite stores the text as sent;
                // so does this, whatever line ending the checkout gave this file.
                migrationBuilder.Sql(table.CreateSql.ReplaceLineEndings(Environment.NewLine) + ";");
                migrationBuilder.Sql($"INSERT INTO \"{temporary}\" ({columns}) SELECT {columns} FROM \"{table.Name}\";");
                migrationBuilder.Sql($"DROP TABLE \"{table.Name}\";");
                migrationBuilder.Sql($"ALTER TABLE \"{temporary}\" RENAME TO \"{table.Name}\";");
                foreach (string index in table.IndexSql)
                {
                    migrationBuilder.Sql(index + ";");
                }
            }
        }
    }
}
