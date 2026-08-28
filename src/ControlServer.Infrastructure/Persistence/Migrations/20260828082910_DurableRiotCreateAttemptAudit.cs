using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // EF migration generator emits inline metadata arrays.

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableRiotCreateAttemptAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CreateAttemptCount",
                table: "OrderIntents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreateAttemptId",
                table: "OrderIntents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreateDispatchArmedAt",
                table: "OrderIntents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DispatchAuditVersion",
                table: "OrderIntents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastCreateOutcome",
                table: "OrderIntents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastCreateOutcomeAt",
                table: "OrderIntents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastCreateReceiptJson",
                table: "OrderIntents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastReconciliationOutcome",
                table: "OrderIntents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastReconciliationOutcomeAt",
                table: "OrderIntents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastReconciliationReceiptJson",
                table: "OrderIntents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RiotDispatchAuditEvents",
                columns: table => new
                {
                    AuditEventId = table.Column<string>(type: "TEXT", nullable: false),
                    MovementLegId = table.Column<string>(type: "TEXT", nullable: false),
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    UpperId = table.Column<string>(type: "TEXT", nullable: false),
                    DispatchGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    AttemptId = table.Column<string>(type: "TEXT", nullable: true),
                    AttemptNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    Phase = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RequestSemanticSha256 = table.Column<string>(type: "TEXT", nullable: true),
                    ReceiptOperation = table.Column<string>(type: "TEXT", nullable: true),
                    ReceiptClassification = table.Column<string>(type: "TEXT", nullable: true),
                    ReceiptObservedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    HttpStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    BusinessCode = table.Column<string>(type: "TEXT", nullable: true),
                    ResultPresent = table.Column<bool>(type: "INTEGER", nullable: true),
                    ReturnedOrderId = table.Column<string>(type: "TEXT", nullable: true),
                    FailureCategory = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiotDispatchAuditEvents", x => x.AuditEventId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderIntents_CreateAttemptId",
                table: "OrderIntents",
                column: "CreateAttemptId",
                unique: true,
                filter: "CreateAttemptId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RiotDispatchAuditEvents_AttemptId",
                table: "RiotDispatchAuditEvents",
                column: "AttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_RiotDispatchAuditEvents_MovementLegId_Sequence",
                table: "RiotDispatchAuditEvents",
                columns: new[] { "MovementLegId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RiotDispatchAuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_OrderIntents_CreateAttemptId",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "CreateAttemptCount",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "CreateAttemptId",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "CreateDispatchArmedAt",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "DispatchAuditVersion",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "LastCreateOutcome",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "LastCreateOutcomeAt",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "LastCreateReceiptJson",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "LastReconciliationOutcome",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "LastReconciliationOutcomeAt",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "LastReconciliationReceiptJson",
                table: "OrderIntents");
        }
    }
}
