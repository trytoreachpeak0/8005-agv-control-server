using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WaitingJourneyWatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                name: "WaitingSince",
                table: "JourneyRuntimes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WaitingWarnedAt",
                table: "JourneyRuntimes",
                type: "TEXT",
                nullable: true);

            // control-server#273: a journey already waiting when the column arrives gets the best start that was recorded, by
            // the same definition of waiting as JourneyWaitClassification (frozen here as it stood when this migration was
            // written; a migration does not change with the code after it). A stationary stage's wait began no later than its
            // reason code's start, else than the row's last write; a travelling stage waits only while it names a reason other
            // than the session gate (ONBOARD_SESSION_NOT_READY, which a real onboard reports on nearly every leg), from that
            // reason's start. Every such start can only be later than the truth, so a wait under way reads shorter and is
            // logged late, never early. No comparison between the two times: SQLite compares these columns as text.
            migrationBuilder.Sql(
                """
                UPDATE "JourneyRuntimes"
                SET "WaitingSince" = COALESCE("BlockReasonSince", "UpdatedAt")
                WHERE "Stage" IN ('AwaitingSublot', 'AwaitingLoadResult', 'AwaitingStationDeparture',
                                  'AwaitingDepartureSafety', 'AwaitingUnloadResult', 'Blocked');
                UPDATE "JourneyRuntimes"
                SET "WaitingSince" = "BlockReasonSince"
                WHERE "Stage" IN ('AwaitingPickupArrival', 'AwaitingGateArrival') AND "BlockReasonCode" IS NOT NULL
                  AND "BlockReasonCode" <> 'ONBOARD_SESSION_NOT_READY';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WaitingBatteryObservedAt",
                table: "JourneyRuntimes");

            migrationBuilder.DropColumn(
                name: "WaitingBatteryPercent",
                table: "JourneyRuntimes");

            migrationBuilder.DropColumn(
                name: "WaitingSince",
                table: "JourneyRuntimes");

            migrationBuilder.DropColumn(
                name: "WaitingWarnedAt",
                table: "JourneyRuntimes");
        }
    }
}
