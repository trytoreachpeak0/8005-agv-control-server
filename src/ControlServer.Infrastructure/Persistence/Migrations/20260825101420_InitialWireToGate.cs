using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialWireToGate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AcceptedDemands",
                columns: table => new
                {
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    TransportDemandKey = table.Column<string>(type: "TEXT", nullable: false),
                    DemandRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    HistoryEpoch = table.Column<string>(type: "TEXT", nullable: false),
                    CatalogRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcceptedDemands", x => x.DemandId);
                });

            migrationBuilder.CreateTable(
                name: "ConnectionRecoveries",
                columns: table => new
                {
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    ActiveUnlockSetJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ResumeAuthorized = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectionRecoveries", x => x.AgvId);
                });

            migrationBuilder.CreateTable(
                name: "OperationResults",
                columns: table => new
                {
                    ResultId = table.Column<string>(type: "TEXT", nullable: false),
                    SlotOperationAttemptId = table.Column<string>(type: "TEXT", nullable: false),
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    ForcedRecoveryGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    HistoricalOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationResults", x => x.ResultId);
                });

            migrationBuilder.CreateTable(
                name: "OrderIntents",
                columns: table => new
                {
                    MovementLegId = table.Column<string>(type: "TEXT", nullable: false),
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    UpperId = table.Column<string>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", nullable: false),
                    TargetStationId = table.Column<string>(type: "TEXT", nullable: false),
                    VehicleKey = table.Column<string>(type: "TEXT", nullable: false),
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    DestinationStationId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgvLifecycleGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    DispatchGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    OrderId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderIntents", x => x.MovementLegId);
                });

            migrationBuilder.CreateTable(
                name: "ProtocolInbox",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    FirstResponseJson = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProtocolInbox", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "ProtocolOutbox",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "TEXT", nullable: false),
                    MessageType = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProtocolOutbox", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "RecoveryDecisions",
                columns: table => new
                {
                    RecoveryActionId = table.Column<string>(type: "TEXT", nullable: false),
                    RecoverySessionId = table.Column<string>(type: "TEXT", nullable: false),
                    ForcedRecoveryGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    Decision = table.Column<string>(type: "TEXT", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecoveryDecisions", x => x.RecoveryActionId);
                });

            migrationBuilder.CreateTable(
                name: "SessionRecoveries",
                columns: table => new
                {
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    ProtocolCommit = table.Column<string>(type: "TEXT", nullable: false),
                    ManifestSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    ProtocolVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CapabilityRevision = table.Column<long>(type: "INTEGER", nullable: true),
                    CapabilityHash = table.Column<string>(type: "TEXT", nullable: true),
                    SafetyRevision = table.Column<long>(type: "INTEGER", nullable: true),
                    SafetyHash = table.Column<string>(type: "TEXT", nullable: true),
                    DepartureSafe = table.Column<bool>(type: "INTEGER", nullable: true),
                    RecoveryReportId = table.Column<string>(type: "TEXT", nullable: true),
                    ForcedRecoveryGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    PendingAttemptIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    PendingResultIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Readiness = table.Column<string>(type: "TEXT", nullable: false),
                    ReasonCode = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionRecoveries", x => x.AgvId);
                });

            migrationBuilder.CreateTable(
                name: "StationOperations",
                columns: table => new
                {
                    SlotOperationAttemptId = table.Column<string>(type: "TEXT", nullable: false),
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    SublotId = table.Column<string>(type: "TEXT", nullable: false),
                    TargetSlotsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ForcedRecoveryGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    EvidenceJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CommittedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StationOperations", x => x.SlotOperationAttemptId);
                });

            migrationBuilder.CreateTable(
                name: "StopClosures",
                columns: table => new
                {
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    CommittedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StopClosures", x => x.DemandId);
                });

            migrationBuilder.CreateTable(
                name: "TransportDemandCompletions",
                columns: table => new
                {
                    TransportDemandKey = table.Column<string>(type: "TEXT", nullable: false),
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    DemandRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Evidence = table.Column<string>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransportDemandCompletions", x => x.TransportDemandKey);
                });

            migrationBuilder.CreateTable(
                name: "UnloadBatches",
                columns: table => new
                {
                    UnloadBatchId = table.Column<string>(type: "TEXT", nullable: false),
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    EvidenceJson = table.Column<string>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnloadBatches", x => x.UnloadBatchId);
                });

            migrationBuilder.CreateTable(
                name: "VehicleRecoveryGenerations",
                columns: table => new
                {
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    ForcedRecoveryGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleRecoveryGenerations", x => x.AgvId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AcceptedDemands_TransportDemandKey",
                table: "AcceptedDemands",
                column: "TransportDemandKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderIntents_UpperId",
                table: "OrderIntents",
                column: "UpperId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransportDemandCompletions_DemandId",
                table: "TransportDemandCompletions",
                column: "DemandId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AcceptedDemands");

            migrationBuilder.DropTable(
                name: "ConnectionRecoveries");

            migrationBuilder.DropTable(
                name: "OperationResults");

            migrationBuilder.DropTable(
                name: "OrderIntents");

            migrationBuilder.DropTable(
                name: "ProtocolInbox");

            migrationBuilder.DropTable(
                name: "ProtocolOutbox");

            migrationBuilder.DropTable(
                name: "RecoveryDecisions");

            migrationBuilder.DropTable(
                name: "SessionRecoveries");

            migrationBuilder.DropTable(
                name: "StationOperations");

            migrationBuilder.DropTable(
                name: "StopClosures");

            migrationBuilder.DropTable(
                name: "TransportDemandCompletions");

            migrationBuilder.DropTable(
                name: "UnloadBatches");

            migrationBuilder.DropTable(
                name: "VehicleRecoveryGenerations");
        }
    }
}
