using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableOperationBusinessOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OperationType",
                table: "StationOperations",
                type: "TEXT",
                nullable: false,
                defaultValue: "Load");

            migrationBuilder.AddColumn<string>(
                name: "EvidenceJson",
                table: "OperationResults",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ObservedAt",
                table: "OperationResults",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "OverallOutcome",
                table: "OperationResults",
                type: "TEXT",
                nullable: false,
                defaultValue: "UNKNOWN");

            migrationBuilder.AddColumn<string>(
                name: "ResultContentSha256",
                table: "OperationResults",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
                "UPDATE OperationResults SET ObservedAt = ReceivedAt;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OperationType",
                table: "StationOperations");

            migrationBuilder.DropColumn(
                name: "EvidenceJson",
                table: "OperationResults");

            migrationBuilder.DropColumn(
                name: "ObservedAt",
                table: "OperationResults");

            migrationBuilder.DropColumn(
                name: "OverallOutcome",
                table: "OperationResults");

            migrationBuilder.DropColumn(
                name: "ResultContentSha256",
                table: "OperationResults");
        }
    }
}
