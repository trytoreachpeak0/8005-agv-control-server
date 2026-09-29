using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // EF migration generator emits inline metadata arrays.

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Batch 9's one migration (control-server#399): the tables batches 9-02 to 9-12 read and write, so that they add none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Seventeen new tables, all empty: the charger roster (versions, chargers, vehicle scopes), the charging policy
    /// (versions, vehicle scopes, approvals, activations), charging cycles, the two holds and their recoveries, station
    /// clearances, the server-held manual charging hold and its records, and the two field confirmation requests.
    /// </para>
    /// <para>
    /// On existing tables, zero behaviour change: <c>StationExclusivities</c> and <c>StationExclusivityRecords</c> accept
    /// <c>CHARGER</c> and gain a nullable <c>ChargerRosterVersion</c> as their last column (a hand-written, column-order
    /// preserving rebuild); <c>OrderIntents</c> gains <c>OrderShape</c>, <c>NOT NULL DEFAULT 'SINGLE_MOVE'</c>, which is
    /// also its back-fill; <c>JourneyRuntimes</c> gains two nullable columns that stay null. Every existing column keeps
    /// its place and every row its values. It runs with journeys in flight.
    /// </para>
    /// </remarks>
    public partial class Batch9ChargingPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Not AddCheckConstraint and AddColumn: on SQLite the CHECK change rebuilds both tables with their columns
            // re-ordered. The rebuild below is the one control-server#386 wrote, with the columns where they were and the
            // new one last. Neither table carries a trigger (the audit triggers are on the two audit tables only).
            OrderPreservingRebuild.Apply(migrationBuilder, StationExclusivitiesWithCharger);
            OrderPreservingRebuild.Apply(migrationBuilder, StationExclusivityRecordsWithCharger);

            // SQLite's own ADD COLUMN, no rebuild: the column goes last and every existing row reads the default, which is
            // the back-fill. No CHECK on it, because adding one would rebuild OrderIntents (control-server#401 validates).
            migrationBuilder.AddColumn<string>(
                name: "OrderShape",
                table: "OrderIntents",
                type: "TEXT",
                nullable: false,
                defaultValue: "SINGLE_MOVE");

            migrationBuilder.AddColumn<long>(
                name: "ChargingPolicyVersion",
                table: "JourneyRuntimes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublishedBatteryState",
                table: "JourneyRuntimes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChargerRosterEntries",
                columns: table => new
                {
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationName = table.Column<string>(type: "TEXT", nullable: false),
                    EntryStationId = table.Column<int>(type: "INTEGER", nullable: true),
                    ExitStationId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargerRosterEntries", x => new { x.Version, x.MapId, x.StationId });
                });

            migrationBuilder.CreateTable(
                name: "ChargerRosterVehicleScopes",
                columns: table => new
                {
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationId = table.Column<int>(type: "INTEGER", nullable: false),
                    VehicleKey = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargerRosterVehicleScopes", x => new { x.Version, x.MapId, x.StationId, x.VehicleKey });
                });

            migrationBuilder.CreateTable(
                name: "ChargerRosterVersions",
                columns: table => new
                {
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<string>(type: "TEXT", nullable: true),
                    LoadedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovalBasis = table.Column<string>(type: "TEXT", nullable: false),
                    ChangeNote = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargerRosterVersions", x => x.Version);
                });

            migrationBuilder.CreateTable(
                name: "ChargingCycles",
                columns: table => new
                {
                    CycleId = table.Column<string>(type: "TEXT", nullable: false),
                    VehicleKey = table.Column<string>(type: "TEXT", nullable: false),
                    JourneyId = table.Column<string>(type: "TEXT", nullable: false),
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChargerRosterVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    ChargingPolicyVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    WireState = table.Column<string>(type: "TEXT", nullable: false),
                    Phase = table.Column<string>(type: "TEXT", nullable: false),
                    UpperId = table.Column<string>(type: "TEXT", nullable: true),
                    AllocatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    OrderConfirmedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ArrivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FirstChargingSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DepartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    EndReason = table.Column<string>(type: "TEXT", nullable: true),
                    ObservationWindowStartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ObservationWindowStartPercent = table.Column<int>(type: "INTEGER", nullable: true),
                    LastSampleAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastSamplePercent = table.Column<int>(type: "INTEGER", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargingCycles", x => x.CycleId);
                    table.CheckConstraint("CK_ChargingCycles_Phase", "\"Phase\" IN ('ACTIVE', 'CLEARING', 'ENDED')");
                    table.CheckConstraint("CK_ChargingCycles_WireState", "\"WireState\" IN ('NOT_CHARGING', 'ALLOCATED', 'EN_ROUTE', 'CHARGING', 'COMPLETE', 'UNABLE_TO_CHARGE', 'UNKNOWN')");
                });

            migrationBuilder.CreateTable(
                name: "ChargingPolicyActivations",
                columns: table => new
                {
                    ActivationId = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ActivatedBy = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargingPolicyActivations", x => x.ActivationId);
                });

            migrationBuilder.CreateTable(
                name: "ChargingPolicyApprovals",
                columns: table => new
                {
                    ApprovalId = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ApprovedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ApproverRole = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    BasisReference = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargingPolicyApprovals", x => x.ApprovalId);
                    table.CheckConstraint("CK_ChargingPolicyApprovals_Source", "\"Source\" IN ('FIELD', 'TEST_FIXTURE', 'L2_PRESET')");
                });

            migrationBuilder.CreateTable(
                name: "ChargingPolicyVehicleScopes",
                columns: table => new
                {
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    VehicleKey = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargingPolicyVehicleScopes", x => new { x.Version, x.VehicleKey });
                });

            migrationBuilder.CreateTable(
                name: "ChargingPolicyVersions",
                columns: table => new
                {
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<string>(type: "TEXT", nullable: true),
                    WrittenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ChangeNote = table.Column<string>(type: "TEXT", nullable: true),
                    MinimumPostTaskBatteryMarginPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    MandatoryChargeEntryThresholdPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    ChargingCompletionThresholdPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    EstimatedTaskConsumptionPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    ProgressStabilizationSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ProgressObservationWindowSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ProgressMinimumIncreasePercent = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargingPolicyVersions", x => x.Version);
                    table.CheckConstraint("CK_ChargingPolicyVersions_ChargingCompletionThresholdPercent", "\"ChargingCompletionThresholdPercent\" BETWEEN 0 AND 100");
                    table.CheckConstraint("CK_ChargingPolicyVersions_EstimatedTaskConsumptionPercent", "\"EstimatedTaskConsumptionPercent\" BETWEEN 0 AND 100");
                    table.CheckConstraint("CK_ChargingPolicyVersions_MandatoryChargeEntryThresholdPercent", "\"MandatoryChargeEntryThresholdPercent\" BETWEEN 0 AND 100");
                    table.CheckConstraint("CK_ChargingPolicyVersions_MinimumPostTaskBatteryMarginPercent", "\"MinimumPostTaskBatteryMarginPercent\" BETWEEN 0 AND 100");
                    table.CheckConstraint("CK_ChargingPolicyVersions_ProgressMinimumIncreasePercent", "\"ProgressMinimumIncreasePercent\" BETWEEN 1 AND 100");
                    table.CheckConstraint("CK_ChargingPolicyVersions_ProgressObservationWindowSeconds", "\"ProgressObservationWindowSeconds\" > 0");
                    table.CheckConstraint("CK_ChargingPolicyVersions_ProgressStabilizationSeconds", "\"ProgressStabilizationSeconds\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "ChargingStationAllocationHolds",
                columns: table => new
                {
                    HoldId = table.Column<string>(type: "TEXT", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", nullable: false),
                    RootCause = table.Column<string>(type: "TEXT", nullable: false),
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChargerRosterVersion = table.Column<long>(type: "INTEGER", nullable: true),
                    VehicleKey = table.Column<string>(type: "TEXT", nullable: true),
                    ReservationRecordId = table.Column<string>(type: "TEXT", nullable: true),
                    CycleId = table.Column<string>(type: "TEXT", nullable: true),
                    UpperId = table.Column<string>(type: "TEXT", nullable: true),
                    OrderId = table.Column<string>(type: "TEXT", nullable: true),
                    ArrivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ChargingStartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FailedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FinalHangAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    HeldAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RawPositionJson = table.Column<string>(type: "TEXT", nullable: true),
                    RawOrderJson = table.Column<string>(type: "TEXT", nullable: true),
                    RawActionResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    RawBatteryJson = table.Column<string>(type: "TEXT", nullable: true),
                    EvidenceReference = table.Column<string>(type: "TEXT", nullable: true),
                    RiotBuild = table.Column<string>(type: "TEXT", nullable: true),
                    RiotContractVersion = table.Column<string>(type: "TEXT", nullable: true),
                    ConfirmedByPersonId = table.Column<string>(type: "TEXT", nullable: true),
                    ConfirmedByRole = table.Column<string>(type: "TEXT", nullable: true),
                    ConfirmedAuthenticatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SiteDisposition = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargingStationAllocationHolds", x => x.HoldId);
                    table.CheckConstraint("CK_ChargingStationAllocationHolds_RootCause", "\"RootCause\" IN ('UNKNOWN')");
                    table.CheckConstraint("CK_ChargingStationAllocationHolds_Trigger", "\"Trigger\" IN ('UNABLE_TO_CHARGE_CONFIRMED', 'INTERRUPTION_CONFIRMED', 'NO_PROGRESS_CONFIRMED', 'MAINTENANCE')");
                });

            migrationBuilder.CreateTable(
                name: "ChargingStationRecoveries",
                columns: table => new
                {
                    RecoveryId = table.Column<string>(type: "TEXT", nullable: false),
                    HoldId = table.Column<string>(type: "TEXT", nullable: false),
                    RecoveredBy = table.Column<string>(type: "TEXT", nullable: false),
                    RecovererRole = table.Column<string>(type: "TEXT", nullable: false),
                    RecoveredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Basis = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargingStationRecoveries", x => x.RecoveryId);
                });

            migrationBuilder.CreateTable(
                name: "ManualChargingHoldRecords",
                columns: table => new
                {
                    HoldId = table.Column<string>(type: "TEXT", nullable: false),
                    VehicleKey = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    Since = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    WarnedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ReleaseRequestId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualChargingHoldRecords", x => x.HoldId);
                });

            migrationBuilder.CreateTable(
                name: "ManualChargingHolds",
                columns: table => new
                {
                    VehicleKey = table.Column<string>(type: "TEXT", nullable: false),
                    HoldId = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    Since = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    WarnedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualChargingHolds", x => x.VehicleKey);
                });

            migrationBuilder.CreateTable(
                name: "ManualStationClearanceConfirmations",
                columns: table => new
                {
                    ConfirmationRequestId = table.Column<string>(type: "TEXT", nullable: false),
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    RequestContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    OperatorId = table.Column<string>(type: "TEXT", nullable: false),
                    VerificationMethod = table.Column<string>(type: "TEXT", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StationId = table.Column<string>(type: "TEXT", nullable: false),
                    PublicStationFunction = table.Column<string>(type: "TEXT", nullable: true),
                    ClearedCondition = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    ProblemReasonCode = table.Column<string>(type: "TEXT", nullable: true),
                    ProblemFieldPath = table.Column<string>(type: "TEXT", nullable: true),
                    ProblemDisplayMessage = table.Column<string>(type: "TEXT", nullable: true),
                    StationReleased = table.Column<bool>(type: "INTEGER", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualStationClearanceConfirmations", x => x.ConfirmationRequestId);
                });

            migrationBuilder.CreateTable(
                name: "StationClearances",
                columns: table => new
                {
                    ClearanceId = table.Column<string>(type: "TEXT", nullable: false),
                    CycleId = table.Column<string>(type: "TEXT", nullable: false),
                    VehicleKey = table.Column<string>(type: "TEXT", nullable: false),
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Proof = table.Column<string>(type: "TEXT", nullable: true),
                    WaitingPointMapId = table.Column<int>(type: "INTEGER", nullable: true),
                    WaitingPointStationId = table.Column<int>(type: "INTEGER", nullable: true),
                    ConfirmedBy = table.Column<string>(type: "TEXT", nullable: true),
                    ConfirmedByRole = table.Column<string>(type: "TEXT", nullable: true),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    VehicleFinalPosition = table.Column<string>(type: "TEXT", nullable: true),
                    OldOrderDisposition = table.Column<string>(type: "TEXT", nullable: true),
                    AssistantsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StationClearances", x => x.ClearanceId);
                    table.CheckConstraint("CK_StationClearances_Proof", "\"Proof\" IN ('ARRIVED_AT_WAITING_POINT', 'MANUAL_CONFIRMATION')");
                });

            migrationBuilder.CreateTable(
                name: "UnableToChargeFieldConfirmations",
                columns: table => new
                {
                    ConfirmationRequestId = table.Column<string>(type: "TEXT", nullable: false),
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    RequestContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    OperatorId = table.Column<string>(type: "TEXT", nullable: false),
                    VerificationMethod = table.Column<string>(type: "TEXT", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ChargerStationId = table.Column<string>(type: "TEXT", nullable: false),
                    ObservedCondition = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    ProblemReasonCode = table.Column<string>(type: "TEXT", nullable: true),
                    ProblemFieldPath = table.Column<string>(type: "TEXT", nullable: true),
                    ProblemDisplayMessage = table.Column<string>(type: "TEXT", nullable: true),
                    ChargingPolicyDecision = table.Column<string>(type: "TEXT", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnableToChargeFieldConfirmations", x => x.ConfirmationRequestId);
                });

            migrationBuilder.CreateTable(
                name: "VehicleChargingEligibilityHolds",
                columns: table => new
                {
                    HoldId = table.Column<string>(type: "TEXT", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", nullable: false),
                    VehicleKey = table.Column<string>(type: "TEXT", nullable: false),
                    CycleId = table.Column<string>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    HeldAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EvidenceReference = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleChargingEligibilityHolds", x => x.HoldId);
                    table.CheckConstraint("CK_VehicleChargingEligibilityHolds_Reason", "\"Reason\" IN ('INTERRUPTION_CONFIRMED', 'NO_PROGRESS_CONFIRMED')");
                });

            migrationBuilder.CreateTable(
                name: "VehicleChargingEligibilityRecoveries",
                columns: table => new
                {
                    RecoveryId = table.Column<string>(type: "TEXT", nullable: false),
                    HoldId = table.Column<string>(type: "TEXT", nullable: false),
                    RecoveredBy = table.Column<string>(type: "TEXT", nullable: false),
                    RecovererRole = table.Column<string>(type: "TEXT", nullable: false),
                    RecoveredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Basis = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleChargingEligibilityRecoveries", x => x.RecoveryId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChargingCycles_JourneyId",
                table: "ChargingCycles",
                column: "JourneyId");

            migrationBuilder.CreateIndex(
                name: "IX_ChargingCycles_MapId_StationId",
                table: "ChargingCycles",
                columns: new[] { "MapId", "StationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChargingCycles_VehicleKey_Open",
                table: "ChargingCycles",
                column: "VehicleKey",
                unique: true,
                filter: "Phase <> 'ENDED'");

            migrationBuilder.CreateIndex(
                name: "IX_ChargingPolicyActivations_Sequence",
                table: "ChargingPolicyActivations",
                column: "Sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChargingPolicyActivations_Version",
                table: "ChargingPolicyActivations",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "IX_ChargingPolicyApprovals_Version",
                table: "ChargingPolicyApprovals",
                column: "Version");

            migrationBuilder.CreateIndex(
                name: "IX_ChargingStationAllocationHolds_IdempotencyKey",
                table: "ChargingStationAllocationHolds",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChargingStationAllocationHolds_MapId_StationId",
                table: "ChargingStationAllocationHolds",
                columns: new[] { "MapId", "StationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChargingStationRecoveries_HoldId",
                table: "ChargingStationRecoveries",
                column: "HoldId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManualChargingHoldRecords_VehicleKey",
                table: "ManualChargingHoldRecords",
                column: "VehicleKey");

            migrationBuilder.CreateIndex(
                name: "IX_ManualChargingHolds_HoldId",
                table: "ManualChargingHolds",
                column: "HoldId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManualStationClearanceConfirmations_RequestMessageId",
                table: "ManualStationClearanceConfirmations",
                column: "RequestMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StationClearances_CycleId",
                table: "StationClearances",
                column: "CycleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StationClearances_MapId_StationId",
                table: "StationClearances",
                columns: new[] { "MapId", "StationId" });

            migrationBuilder.CreateIndex(
                name: "IX_UnableToChargeFieldConfirmations_RequestMessageId",
                table: "UnableToChargeFieldConfirmations",
                column: "RequestMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleChargingEligibilityHolds_IdempotencyKey",
                table: "VehicleChargingEligibilityHolds",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleChargingEligibilityHolds_VehicleKey",
                table: "VehicleChargingEligibilityHolds",
                column: "VehicleKey");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleChargingEligibilityRecoveries_HoldId",
                table: "VehicleChargingEligibilityRecoveries",
                column: "HoldId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChargerRosterEntries");

            migrationBuilder.DropTable(
                name: "ChargerRosterVehicleScopes");

            migrationBuilder.DropTable(
                name: "ChargerRosterVersions");

            migrationBuilder.DropTable(
                name: "ChargingCycles");

            migrationBuilder.DropTable(
                name: "ChargingPolicyActivations");

            migrationBuilder.DropTable(
                name: "ChargingPolicyApprovals");

            migrationBuilder.DropTable(
                name: "ChargingPolicyVehicleScopes");

            migrationBuilder.DropTable(
                name: "ChargingPolicyVersions");

            migrationBuilder.DropTable(
                name: "ChargingStationAllocationHolds");

            migrationBuilder.DropTable(
                name: "ChargingStationRecoveries");

            migrationBuilder.DropTable(
                name: "ManualChargingHoldRecords");

            migrationBuilder.DropTable(
                name: "ManualChargingHolds");

            migrationBuilder.DropTable(
                name: "ManualStationClearanceConfirmations");

            migrationBuilder.DropTable(
                name: "StationClearances");

            migrationBuilder.DropTable(
                name: "UnableToChargeFieldConfirmations");

            migrationBuilder.DropTable(
                name: "VehicleChargingEligibilityHolds");

            migrationBuilder.DropTable(
                name: "VehicleChargingEligibilityRecoveries");

            // A CHARGER row left in either table makes the old CHECK refuse the copy, and the whole Down goes back.
            OrderPreservingRebuild.Apply(migrationBuilder, StationExclusivitiesAtBatch8);
            OrderPreservingRebuild.Apply(migrationBuilder, StationExclusivityRecordsAtBatch8);

            // SQLite's own DROP COLUMN rather than EF's rebuild, which would re-order the other columns of both tables. The
            // columns are the last ones, so dropping them gives back the stored definition Up found.
            migrationBuilder.Sql("""ALTER TABLE "OrderIntents" DROP COLUMN "OrderShape";""");
            migrationBuilder.Sql("""ALTER TABLE "JourneyRuntimes" DROP COLUMN "PublishedBatteryState";""");
            migrationBuilder.Sql("""ALTER TABLE "JourneyRuntimes" DROP COLUMN "ChargingPolicyVersion";""");
        }

        // Table definitions exactly as SQLite stored them at 20260929070322_Batch8RetireOldVehicleOccupancy (read back from
        // sqlite_master), and as this migration leaves them. Each is written under a temporary name that the final rename
        // replaces, so the stored text of the renamed table is this text with the real name. The copy lists name the columns
        // the table had before; the new ChargerRosterVersion starts null.

        private static readonly OrderPreservingRebuild.Table StationExclusivitiesAtBatch8 = new(
            "StationExclusivities",
            """
            CREATE TABLE "ef_temp_StationExclusivities" (
                "MapId" INTEGER NOT NULL,
                "StationId" INTEGER NOT NULL,
                "StationKind" TEXT NOT NULL,
                "State" TEXT NOT NULL,
                "VehicleKey" TEXT NOT NULL,
                "JourneyId" TEXT NOT NULL,
                "StateSince" TEXT NOT NULL,
                "WaitingPointVersion" INTEGER NULL,
                "RecordId" TEXT NOT NULL,
                CONSTRAINT "PK_StationExclusivities" PRIMARY KEY ("MapId", "StationId"),
                CONSTRAINT "CK_StationExclusivities_State" CHECK ("State" IN ('RESERVED', 'OCCUPIED')),
                CONSTRAINT "CK_StationExclusivities_StationKind" CHECK ("StationKind" IN ('WAITING_POINT', 'FIXED_TASK_STATION'))
            )
            """,
            ["MapId", "StationId", "StationKind", "State", "VehicleKey", "JourneyId", "StateSince", "WaitingPointVersion", "RecordId"],
            [
                """CREATE INDEX "IX_StationExclusivities_JourneyId" ON "StationExclusivities" ("JourneyId")""",
                """CREATE UNIQUE INDEX "IX_StationExclusivities_RecordId" ON "StationExclusivities" ("RecordId")""",
                """CREATE INDEX "IX_StationExclusivities_VehicleKey" ON "StationExclusivities" ("VehicleKey")""",
            ]);

        private static readonly OrderPreservingRebuild.Table StationExclusivitiesWithCharger =
            StationExclusivitiesAtBatch8 with
            {
                CreateSql = """
                    CREATE TABLE "ef_temp_StationExclusivities" (
                        "MapId" INTEGER NOT NULL,
                        "StationId" INTEGER NOT NULL,
                        "StationKind" TEXT NOT NULL,
                        "State" TEXT NOT NULL,
                        "VehicleKey" TEXT NOT NULL,
                        "JourneyId" TEXT NOT NULL,
                        "StateSince" TEXT NOT NULL,
                        "WaitingPointVersion" INTEGER NULL,
                        "RecordId" TEXT NOT NULL,
                        "ChargerRosterVersion" INTEGER NULL,
                        CONSTRAINT "PK_StationExclusivities" PRIMARY KEY ("MapId", "StationId"),
                        CONSTRAINT "CK_StationExclusivities_State" CHECK ("State" IN ('RESERVED', 'OCCUPIED')),
                        CONSTRAINT "CK_StationExclusivities_StationKind" CHECK ("StationKind" IN ('WAITING_POINT', 'FIXED_TASK_STATION', 'CHARGER'))
                    )
                    """
            };

        private static readonly OrderPreservingRebuild.Table StationExclusivityRecordsAtBatch8 = new(
            "StationExclusivityRecords",
            """
            CREATE TABLE "ef_temp_StationExclusivityRecords" (
                "RecordId" TEXT NOT NULL CONSTRAINT "PK_StationExclusivityRecords" PRIMARY KEY,
                "MapId" INTEGER NOT NULL,
                "StationId" INTEGER NOT NULL,
                "StationKind" TEXT NOT NULL,
                "VehicleKey" TEXT NOT NULL,
                "JourneyId" TEXT NOT NULL,
                "WaitingPointVersion" INTEGER NULL,
                "ReservedAt" TEXT NULL,
                "OccupiedAt" TEXT NULL,
                "ReleasedAt" TEXT NULL,
                "ReleaseReason" TEXT NULL,
                CONSTRAINT "CK_StationExclusivityRecords_StationKind" CHECK ("StationKind" IN ('WAITING_POINT', 'FIXED_TASK_STATION'))
            )
            """,
            ["RecordId", "MapId", "StationId", "StationKind", "VehicleKey", "JourneyId", "WaitingPointVersion", "ReservedAt", "OccupiedAt", "ReleasedAt", "ReleaseReason"],
            [
                """CREATE INDEX "IX_StationExclusivityRecords_JourneyId" ON "StationExclusivityRecords" ("JourneyId")""",
                """CREATE INDEX "IX_StationExclusivityRecords_MapId_StationId" ON "StationExclusivityRecords" ("MapId", "StationId")""",
                """CREATE INDEX "IX_StationExclusivityRecords_VehicleKey" ON "StationExclusivityRecords" ("VehicleKey")""",
            ]);

        private static readonly OrderPreservingRebuild.Table StationExclusivityRecordsWithCharger =
            StationExclusivityRecordsAtBatch8 with
            {
                CreateSql = """
                    CREATE TABLE "ef_temp_StationExclusivityRecords" (
                        "RecordId" TEXT NOT NULL CONSTRAINT "PK_StationExclusivityRecords" PRIMARY KEY,
                        "MapId" INTEGER NOT NULL,
                        "StationId" INTEGER NOT NULL,
                        "StationKind" TEXT NOT NULL,
                        "VehicleKey" TEXT NOT NULL,
                        "JourneyId" TEXT NOT NULL,
                        "WaitingPointVersion" INTEGER NULL,
                        "ReservedAt" TEXT NULL,
                        "OccupiedAt" TEXT NULL,
                        "ReleasedAt" TEXT NULL,
                        "ReleaseReason" TEXT NULL,
                        "ChargerRosterVersion" INTEGER NULL,
                        CONSTRAINT "CK_StationExclusivityRecords_StationKind" CHECK ("StationKind" IN ('WAITING_POINT', 'FIXED_TASK_STATION', 'CHARGER'))
                    )
                    """
            };

        /// <summary>
        /// The table rebuild EF performs for a change SQLite cannot ALTER, keeping the columns in the order they had
        /// (the same helper as <c>Batch8VehiclePurposePersistence</c>'s; a migration keeps its own copy so that it never
        /// changes with a later one).
        /// </summary>
        /// <remarks>
        /// No table in this schema has a foreign key, so none of EF's <c>PRAGMA foreign_keys</c> handling is needed, and the
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
