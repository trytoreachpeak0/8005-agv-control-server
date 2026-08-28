using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExperimentalAbsentObservationCreateAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EligibilityBasis",
                table: "RiotDispatchAuditEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExperimentalAuthorizationId",
                table: "RiotDispatchAuditEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DispatchAuditSequence",
                table: "OrderIntents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExperimentalCreateAuthorizationId",
                table: "OrderIntents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExperimentalRiotCreateAuthorizations",
                columns: table => new
                {
                    AuthorizationId = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorizationVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    UpperId = table.Column<string>(type: "TEXT", nullable: false),
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    MovementLegId = table.Column<string>(type: "TEXT", nullable: false),
                    AgvLifecycleGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    DispatchGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PersistedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ConsumedByAttemptId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentalRiotCreateAuthorizations", x => x.AuthorizationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RiotDispatchAuditEvents_ExperimentalAuthorizationId",
                table: "RiotDispatchAuditEvents",
                column: "ExperimentalAuthorizationId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderIntents_ExperimentalCreateAuthorizationId",
                table: "OrderIntents",
                column: "ExperimentalCreateAuthorizationId",
                unique: true,
                filter: "ExperimentalCreateAuthorizationId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentalRiotCreateAuthorizations_ConsumedByAttemptId",
                table: "ExperimentalRiotCreateAuthorizations",
                column: "ConsumedByAttemptId",
                unique: true,
                filter: "ConsumedByAttemptId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentalRiotCreateAuthorizations_UpperId",
                table: "ExperimentalRiotCreateAuthorizations",
                column: "UpperId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExperimentalRiotCreateAuthorizations");

            migrationBuilder.DropIndex(
                name: "IX_RiotDispatchAuditEvents_ExperimentalAuthorizationId",
                table: "RiotDispatchAuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_OrderIntents_ExperimentalCreateAuthorizationId",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "EligibilityBasis",
                table: "RiotDispatchAuditEvents");

            migrationBuilder.DropColumn(
                name: "ExperimentalAuthorizationId",
                table: "RiotDispatchAuditEvents");

            migrationBuilder.DropColumn(
                name: "DispatchAuditSequence",
                table: "OrderIntents");

            migrationBuilder.DropColumn(
                name: "ExperimentalCreateAuthorizationId",
                table: "OrderIntents");
        }
    }
}
