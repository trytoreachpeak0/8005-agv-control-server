using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableRecoveryStateMachineG2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActiveUnlockSlotsJson",
                table: "SessionRecoveries",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "ProvenRecoveryCheckpoint",
                table: "SessionRecoveries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReportedForcedRecoveryGeneration",
                table: "SessionRecoveries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "UnsettledSlotOperationAttemptId",
                table: "SessionRecoveries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FencedAt",
                table: "ProtocolOutbox",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExceptionRecoverySessions",
                columns: table => new
                {
                    ExceptionRecoverySessionId = table.Column<string>(type: "TEXT", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    RequestContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    DemandId = table.Column<string>(type: "TEXT", nullable: true),
                    SlotsJson = table.Column<string>(type: "TEXT", nullable: false),
                    AdministratorId = table.Column<string>(type: "TEXT", nullable: false),
                    AdministratorRole = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    SelectedAction = table.Column<string>(type: "TEXT", nullable: true),
                    ForcedRecoveryGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    OpenedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExceptionRecoverySessions", x => x.ExceptionRecoverySessionId);
                });

            migrationBuilder.CreateTable(
                name: "HardwareRecoveryRecords",
                columns: table => new
                {
                    RecordId = table.Column<string>(type: "TEXT", nullable: false),
                    ExceptionRecoverySessionId = table.Column<string>(type: "TEXT", nullable: false),
                    RecoveryActionId = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    OperatorId = table.Column<string>(type: "TEXT", nullable: false),
                    AdministratorRole = table.Column<string>(type: "TEXT", nullable: false),
                    SlotsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ChecksJson = table.Column<string>(type: "TEXT", nullable: false),
                    ActionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ObservationsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HardwareRecoveryRecords", x => x.RecordId);
                });

            migrationBuilder.CreateTable(
                name: "RecoveryResultEvidence",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkflowId = table.Column<string>(type: "TEXT", nullable: false),
                    MessageType = table.Column<string>(type: "TEXT", nullable: false),
                    ForcedRecoveryGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    HistoricalOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecoveryResultEvidence", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "RecoveryWorkflows",
                columns: table => new
                {
                    WorkflowId = table.Column<string>(type: "TEXT", nullable: false),
                    WorkflowType = table.Column<string>(type: "TEXT", nullable: false),
                    ExceptionRecoverySessionId = table.Column<string>(type: "TEXT", nullable: true),
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    DemandId = table.Column<string>(type: "TEXT", nullable: true),
                    SlotOperationAttemptId = table.Column<string>(type: "TEXT", nullable: true),
                    SlotsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ForcedRecoveryGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    RequestMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    RequestContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    CommandMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    CommandMessageType = table.Column<string>(type: "TEXT", nullable: true),
                    CommandContentHash = table.Column<string>(type: "TEXT", nullable: true),
                    HandoffId = table.Column<string>(type: "TEXT", nullable: true),
                    ResultMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    ResultContentHash = table.Column<string>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecoveryWorkflows", x => x.WorkflowId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExceptionRecoverySessions_RequestId",
                table: "ExceptionRecoverySessions",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryWorkflows_CommandMessageId",
                table: "RecoveryWorkflows",
                column: "CommandMessageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExceptionRecoverySessions");

            migrationBuilder.DropTable(
                name: "HardwareRecoveryRecords");

            migrationBuilder.DropTable(
                name: "RecoveryResultEvidence");

            migrationBuilder.DropTable(
                name: "RecoveryWorkflows");

            migrationBuilder.DropColumn(
                name: "ActiveUnlockSlotsJson",
                table: "SessionRecoveries");

            migrationBuilder.DropColumn(
                name: "ProvenRecoveryCheckpoint",
                table: "SessionRecoveries");

            migrationBuilder.DropColumn(
                name: "ReportedForcedRecoveryGeneration",
                table: "SessionRecoveries");

            migrationBuilder.DropColumn(
                name: "UnsettledSlotOperationAttemptId",
                table: "SessionRecoveries");

            migrationBuilder.DropColumn(
                name: "FencedAt",
                table: "ProtocolOutbox");
        }
    }
}
