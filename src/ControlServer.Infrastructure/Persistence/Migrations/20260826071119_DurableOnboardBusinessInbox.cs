using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableOnboardBusinessInbox : Migration
    {
        private static readonly string[] OperationResultIdentityColumns =
            ["SlotOperationAttemptId", "ForcedRecoveryGeneration"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MessageType",
                table: "ProtocolInbox",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RequestJson",
                table: "ProtocolInbox",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_OperationResults_SlotOperationAttemptId_ForcedRecoveryGeneration",
                table: "OperationResults",
                columns: OperationResultIdentityColumns,
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OperationResults_SlotOperationAttemptId_ForcedRecoveryGeneration",
                table: "OperationResults");

            migrationBuilder.DropColumn(
                name: "MessageType",
                table: "ProtocolInbox");

            migrationBuilder.DropColumn(
                name: "RequestJson",
                table: "ProtocolInbox");
        }
    }
}
