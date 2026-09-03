using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ResumeReplacementOperationResult : Migration
    {
        private static readonly string[] AttemptGenerationColumns =
            ["SlotOperationAttemptId", "ForcedRecoveryGeneration"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OperationResults_SlotOperationAttemptId_ForcedRecoveryGeneration",
                table: "OperationResults");

            migrationBuilder.AddColumn<string>(
                name: "SupersededByResultId",
                table: "OperationResults",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperationResults_SlotOperationAttemptId_ForcedRecoveryGeneration",
                table: "OperationResults",
                columns: AttemptGenerationColumns,
                unique: true,
                filter: "SupersededByResultId IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OperationResults_SlotOperationAttemptId_ForcedRecoveryGeneration",
                table: "OperationResults");

            migrationBuilder.DropColumn(
                name: "SupersededByResultId",
                table: "OperationResults");

            migrationBuilder.CreateIndex(
                name: "IX_OperationResults_SlotOperationAttemptId_ForcedRecoveryGeneration",
                table: "OperationResults",
                columns: AttemptGenerationColumns,
                unique: true);
        }
    }
}
