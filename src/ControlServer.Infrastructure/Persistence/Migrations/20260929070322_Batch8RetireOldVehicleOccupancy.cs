using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Batch 8-16 (control-server#387), the second batch-8 migration: a vehicle's occupancy is its <c>VehiclePurposeClaims</c>
    /// row alone, so the dispatch lease table and the order occupancy columns on <c>OrderIntents</c> go.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It checks before it drops.</b> An unreleased lease with no purpose claim of the same journey on the same vehicle,
    /// or an unreleased order occupancy whose journey on that same vehicle is neither still open nor still holding a claim on
    /// it (another vehicle's journey does not excuse it), is a place where
    /// the old occupancy and the claim already disagree. Moving it over would make that unexplained state legal, and
    /// dropping it would lose it; so the migration refuses, whole, naming every such row, and drops nothing. A person finds
    /// out why. Do not edit this migration to get past it.
    /// </para>
    /// <para>
    /// What does not refuse it: a journey in flight (its lease and its claim were written and are released together), and
    /// the one round in which a normal unload has released the claim while the journey is not yet <c>Completed</c> (the
    /// order occupancy used to be released one round later, with the journey).
    /// </para>
    /// <para>
    /// <b>It backfills the claim history in the same migration that moves the engine onto the one write path</b>
    /// (<c>VehiclePurposeClaimWrites</c>), handed over from control-server#386: one open record per claim held now, one
    /// closed record per released lease whose journey has no record yet (<c>DISPATCH_LEASE_RELEASED</c>, the lease's own
    /// moments), and any record left open without its claim is closed (<c>CLAIM_GONE_BEFORE_MIGRATION</c>; only a Down run
    /// under the old engine can leave one). So no journey gets two records, and none stays open once its claim is gone.
    /// Record ids are derived from the vehicle and the journey, so running Up again after a Down writes the same rows.
    /// </para>
    /// <para>
    /// The columns are dropped with SQLite's own <c>ALTER TABLE ... DROP COLUMN</c> rather than EF's table rebuild, which
    /// would re-order every other column of <c>OrderIntents</c> (control-server#386). They are its last two, so Down's
    /// <c>ADD COLUMN</c> puts them back where they were.
    /// </para>
    /// <para>
    /// <b>Down's known limits.</b> It rebuilds one lease per transport journey from that journey's latest record, with the
    /// demand read from the journey row (or, for an acceptance from before journeys existed, from the journey id). The
    /// order occupancy columns come back empty: the old engine then finds no claimed order for the journeys that were in
    /// flight, which only means their occupancy is not held twice. The records stay.
    /// </para>
    /// </remarks>
    public partial class Batch8RetireOldVehicleOccupancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RefuseUnfinishedOldOccupancySql.ReplaceLineEndings(Environment.NewLine));
            migrationBuilder.Sql(BackfillClaimHistorySql.ReplaceLineEndings(Environment.NewLine));

            migrationBuilder.DropTable(
                name: "VehicleDispatchLeases");

            migrationBuilder.DropIndex(
                name: "IX_OrderIntents_VehicleKey",
                table: "OrderIntents");

            migrationBuilder.Sql("""ALTER TABLE "OrderIntents" DROP COLUMN "VehicleOccupancyReleasedAt";""");
            migrationBuilder.Sql("""ALTER TABLE "OrderIntents" DROP COLUMN "VehicleOccupancyClaimedAt";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VehicleOccupancyClaimedAt",
                table: "OrderIntents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VehicleOccupancyReleasedAt",
                table: "OrderIntents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VehicleDispatchLeases",
                columns: table => new
                {
                    JourneyId = table.Column<string>(type: "TEXT", nullable: false),
                    AcquiredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    VehicleKey = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleDispatchLeases", x => x.JourneyId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderIntents_VehicleKey",
                table: "OrderIntents",
                column: "VehicleKey",
                unique: true,
                filter: "VehicleOccupancyClaimedAt IS NOT NULL AND VehicleOccupancyReleasedAt IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleDispatchLeases_DemandId",
                table: "VehicleDispatchLeases",
                column: "DemandId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleDispatchLeases_VehicleKey",
                table: "VehicleDispatchLeases",
                column: "VehicleKey",
                unique: true,
                filter: "ReleasedAt IS NULL");

            migrationBuilder.Sql(RebuildLeasesFromRecordsSql.ReplaceLineEndings(Environment.NewLine));
        }

        /// <summary>
        /// Fails the migration, whole, with a message naming every unfinished old occupancy that has no claim behind it.
        /// </summary>
        /// <remarks>
        /// SQLite has no statement that fails on a condition outside a trigger, so the check inserts its message into a
        /// temporary table whose trigger raises it; when nothing is wrong the <c>HAVING</c> leaves nothing to insert.
        /// </remarks>
        internal const string RefuseUnfinishedOldOccupancySql = """
            CREATE TEMP TABLE "ef_guard_Batch8RetireOldVehicleOccupancy" ("Message" TEXT NOT NULL);
            CREATE TEMP TRIGGER "ef_guard_Batch8RetireOldVehicleOccupancy_refuse"
            BEFORE INSERT ON "ef_guard_Batch8RetireOldVehicleOccupancy"
            BEGIN
                SELECT RAISE(ABORT, NEW."Message");
            END;
            INSERT INTO "ef_guard_Batch8RetireOldVehicleOccupancy" ("Message")
            SELECT 'Batch8RetireOldVehicleOccupancy refused, nothing was dropped: ' || count(*)
                || ' unfinished old vehicle occupancy row(s) have no purpose claim behind them. Find out why before upgrading; '
                || 'do not edit the migration. ' || group_concat("Row", '; ')
            FROM (
                SELECT 'VehicleDispatchLeases unreleased: DemandId=' || l."DemandId" || ' JourneyId=' || l."JourneyId"
                    || ' VehicleKey=' || l."VehicleKey" AS "Row"
                FROM "VehicleDispatchLeases" AS l
                WHERE l."ReleasedAt" IS NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM "VehiclePurposeClaims" AS c
                      WHERE c."VehicleKey" = l."VehicleKey" AND c."JourneyId" = l."JourneyId")
                UNION ALL
                SELECT 'OrderIntents occupancy unreleased: UpperId=' || o."UpperId" || ' DemandId=' || coalesce(o."DemandId", '')
                    || ' VehicleKey=' || o."VehicleKey"
                FROM "OrderIntents" AS o
                WHERE o."VehicleOccupancyClaimedAt" IS NOT NULL AND o."VehicleOccupancyReleasedAt" IS NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM "JourneyRuntimes" AS j
                      WHERE j."PickupUpperId" = o."UpperId" AND j."VehicleKey" = o."VehicleKey"
                        AND (j."Stage" <> 'Completed'
                             OR EXISTS (
                                 SELECT 1 FROM "VehiclePurposeClaims" AS c
                                 WHERE c."JourneyId" = j."JourneyId" AND c."VehicleKey" = o."VehicleKey")))
            )
            HAVING count(*) > 0;
            DROP TRIGGER "ef_guard_Batch8RetireOldVehicleOccupancy_refuse";
            DROP TABLE "ef_guard_Batch8RetireOldVehicleOccupancy";
            """;

        internal const string BackfillClaimHistorySql = """
            UPDATE "VehiclePurposeClaimRecords" AS r
            SET "ReleasedAt" = coalesce(
                    (SELECT l."ReleasedAt" FROM "VehicleDispatchLeases" AS l
                     WHERE l."JourneyId" = r."JourneyId" AND l."VehicleKey" = r."VehicleKey" AND l."ReleasedAt" IS NOT NULL),
                    strftime('%Y-%m-%d %H:%M:%S+00:00', 'now')),
                "ReleaseReason" = 'CLAIM_GONE_BEFORE_MIGRATION'
            WHERE r."ReleasedAt" IS NULL
              AND NOT EXISTS (
                  SELECT 1 FROM "VehiclePurposeClaims" AS c
                  WHERE c."VehicleKey" = r."VehicleKey" AND c."JourneyId" = r."JourneyId");
            INSERT INTO "VehiclePurposeClaimRecords"
                ("RecordId", "VehicleKey", "Purpose", "JourneyId", "AcquiredAt", "ReleasedAt", "ReleaseReason")
            SELECT 'backfill:' || c."VehicleKey" || ':' || c."JourneyId", c."VehicleKey", c."Purpose", c."JourneyId",
                   c."ClaimedAt", NULL, NULL
            FROM "VehiclePurposeClaims" AS c
            WHERE NOT EXISTS (
                SELECT 1 FROM "VehiclePurposeClaimRecords" AS r
                WHERE r."VehicleKey" = c."VehicleKey" AND r."JourneyId" = c."JourneyId" AND r."ReleasedAt" IS NULL);
            INSERT INTO "VehiclePurposeClaimRecords"
                ("RecordId", "VehicleKey", "Purpose", "JourneyId", "AcquiredAt", "ReleasedAt", "ReleaseReason")
            SELECT 'lease:' || l."VehicleKey" || ':' || l."JourneyId", l."VehicleKey", 'TRANSPORT', l."JourneyId",
                   l."AcquiredAt", l."ReleasedAt", 'DISPATCH_LEASE_RELEASED'
            FROM "VehicleDispatchLeases" AS l
            WHERE l."ReleasedAt" IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM "VehiclePurposeClaimRecords" AS r
                  WHERE r."VehicleKey" = l."VehicleKey" AND r."JourneyId" = l."JourneyId");
            """;

        internal const string RebuildLeasesFromRecordsSql = """
            INSERT INTO "VehicleDispatchLeases" ("JourneyId", "DemandId", "VehicleKey", "AcquiredAt", "ReleasedAt")
            SELECT r."JourneyId",
                   coalesce(
                       (SELECT j."DemandId" FROM "JourneyRuntimes" AS j WHERE j."JourneyId" = r."JourneyId"),
                       substr(r."JourneyId", length('journey:') + 1)),
                   r."VehicleKey", r."AcquiredAt", r."ReleasedAt"
            FROM "VehiclePurposeClaimRecords" AS r
            WHERE r."Purpose" = 'TRANSPORT'
              AND r."RecordId" = (
                  SELECT latest."RecordId" FROM "VehiclePurposeClaimRecords" AS latest
                  WHERE latest."JourneyId" = r."JourneyId" AND latest."Purpose" = 'TRANSPORT'
                  ORDER BY latest."ReleasedAt" IS NULL DESC, latest."AcquiredAt" DESC, latest."RecordId" DESC
                  LIMIT 1);
            """;
    }
}
