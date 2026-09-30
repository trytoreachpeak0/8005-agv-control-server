using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Batch8SlotFaultDeclarations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SlotFaultDeclarations",
                columns: table => new
                {
                    DeclarationId = table.Column<string>(type: "TEXT", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    RequestContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    AgvId = table.Column<string>(type: "TEXT", nullable: false),
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    SlotOperationAttemptId = table.Column<string>(type: "TEXT", nullable: false),
                    OperationType = table.Column<string>(type: "TEXT", nullable: false),
                    SlotNo = table.Column<int>(type: "INTEGER", nullable: false),
                    FaultCategory = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: false),
                    AdministratorId = table.Column<string>(type: "TEXT", nullable: false),
                    AdministratorRole = table.Column<string>(type: "TEXT", nullable: false),
                    DeclaredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ReadingsJson = table.Column<string>(type: "TEXT", nullable: true),
                    CommandMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    ResultMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    ResultOutcome = table.Column<string>(type: "TEXT", nullable: true),
                    ResultProblemJson = table.Column<string>(type: "TEXT", nullable: true),
                    ResultReceivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlotFaultDeclarations", x => x.DeclarationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SlotFaultDeclarations_AgvId",
                table: "SlotFaultDeclarations",
                column: "AgvId");

            migrationBuilder.CreateIndex(
                name: "IX_SlotFaultDeclarations_CommandMessageId",
                table: "SlotFaultDeclarations",
                column: "CommandMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlotFaultDeclarations_PendingAttempt",
                table: "SlotFaultDeclarations",
                column: "SlotOperationAttemptId",
                unique: true,
                filter: "State = 'PENDING'");

            migrationBuilder.CreateIndex(
                name: "IX_SlotFaultDeclarations_RequestId",
                table: "SlotFaultDeclarations",
                column: "RequestId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SlotFaultDeclarations");
        }
    }
}
