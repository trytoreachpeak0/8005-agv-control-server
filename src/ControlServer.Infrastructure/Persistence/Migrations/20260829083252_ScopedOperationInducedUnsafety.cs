using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopedOperationInducedUnsafety : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SafetyReasonCodesJson",
                table: "SessionRecoveries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SafetyUnknownPresent",
                table: "SessionRecoveries",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SafetyReasonCodesJson",
                table: "SessionRecoveries");

            migrationBuilder.DropColumn(
                name: "SafetyUnknownPresent",
                table: "SessionRecoveries");
        }
    }
}
