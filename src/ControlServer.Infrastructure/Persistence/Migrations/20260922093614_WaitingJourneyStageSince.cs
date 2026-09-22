using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WaitingJourneyStageSince : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StageSince",
                table: "JourneyRuntimes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WaitingBatteryObservedAt",
                table: "JourneyRuntimes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WaitingBatteryPercent",
                table: "JourneyRuntimes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WaitingWarnedAt",
                table: "JourneyRuntimes",
                type: "TEXT",
                nullable: true);

            // control-server#273: a journey under way when the column arrives gets the best start that was recorded. Nobody
            // wrote when its stage began; a reason code's start is the closest (SetStage clears it, so it is never earlier
            // than the stage it belongs to -- except a block escalated from an AREA machine hold, which keeps the hold's
            // start, and the vehicle has stood there since). Without one, the last write to the row. Both can only be
            // later than the truth, so a wait under way reads shorter than it is and is logged late, never early. No
            // comparison between the two: SQLite compares these columns as text.
            migrationBuilder.Sql(
                """
                UPDATE "JourneyRuntimes"
                SET "StageSince" = COALESCE("BlockReasonSince", "UpdatedAt")
                WHERE "Stage" <> 'Completed';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StageSince",
                table: "JourneyRuntimes");

            migrationBuilder.DropColumn(
                name: "WaitingBatteryObservedAt",
                table: "JourneyRuntimes");

            migrationBuilder.DropColumn(
                name: "WaitingBatteryPercent",
                table: "JourneyRuntimes");

            migrationBuilder.DropColumn(
                name: "WaitingWarnedAt",
                table: "JourneyRuntimes");
        }
    }
}
