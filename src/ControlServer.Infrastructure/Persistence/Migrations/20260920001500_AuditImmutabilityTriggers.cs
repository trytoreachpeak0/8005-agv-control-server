using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// control-server#199: the write-once rule for audit, moved from the EF layer down into the database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="AuditImmutabilityGuard"/> inspects the change tracker before <c>SaveChanges</c>, so it only ever sees
    /// writes that go through it. Raw SQL, <c>ExecuteUpdateAsync</c>, <c>ExecuteDeleteAsync</c> and a second connection
    /// opened straight onto the file all walk past it (control-server#161 review O1). These six triggers put the rule on
    /// the tables themselves, where every one of those paths has to meet it.
    /// </para>
    /// <para>
    /// <b>UPDATE is refused outright.</b> There is no retention question to ask: an audit record is never rewritten, at
    /// any age, for any reason.
    /// </para>
    /// <para>
    /// <b>REPLACE is refused too, and it needs a trigger of its own</b> (control-server#199 review S2).
    /// <c>REPLACE INTO</c> / <c>INSERT OR REPLACE</c> is an INSERT statement, so <c>TR_*_NoUpdate</c> never sees it;
    /// and the implicit delete it performs to resolve the primary-key conflict does <b>not</b> fire a
    /// <c>BEFORE DELETE</c> trigger unless <c>PRAGMA recursive_triggers</c> is ON, which is not SQLite's default and is
    /// set nowhere in this server. Without the third trigger, a raw
    /// <c>REPLACE INTO "BusinessAuditRecords" (...) VALUES ('&lt;existing id&gt;', ...)</c> rewrites an audit record of
    /// any age, whole, with neither of the other two making a sound. <c>TR_*_NoReplace</c> closes it by refusing any
    /// INSERT whose key already exists. It costs one primary-key lookup per insert and refuses nothing a plain INSERT
    /// could have done -- a plain INSERT onto an existing key fails on the key anyway.
    /// </para>
    /// <para>
    /// <b>DELETE is refused only inside the 180 day floor</b> (REQ-0271), so that
    /// <c>GovernanceStore.PurgeExpiredAuditAsync</c> keeps working. A trigger cannot read configuration, so it cannot know
    /// about a deployment that configured retention longer than the floor; that longer period stays the EF layer's to
    /// enforce. The division is deliberate: the database holds the line nobody may cross, the application holds the line
    /// this deployment drew for itself. And the application's line is never the looser of the two:
    /// <c>GovernanceModule.ResolveRetentionPolicy</c> refuses a configured value below the floor at startup, so a running
    /// server's EF-layer check is always at least as strict as this trigger.
    /// </para>
    /// <para>
    /// <b>How the trigger gets "now".</b> From <c>julianday('now')</c> -- the database machine's clock -- converted to
    /// .NET ticks so it can be compared with <c>RecordedAtUtcTicks</c>:
    /// <c>(julianday('now') - 1721425.5) * 864000000000</c>, where 1721425.5 is the Julian day of 0001-01-01T00:00:00Z
    /// (the .NET tick epoch) and 864000000000 is the ticks in a day. Both sides are UTC: SQLite's <c>'now'</c> is UTC by
    /// definition (local time would need an explicit <c>'localtime'</c> modifier) and <c>RecordedAtUtcTicks</c> is written
    /// from <c>DateTimeOffset.UtcTicks</c>. The double loses about 128 ticks (13 microseconds) of precision at this
    /// magnitude, which is nothing against a 180 day boundary. The alternative -- refuse every delete and let the purge
    /// write an authorising row inside its own transaction -- was rejected: that row is itself writable by the raw SQL
    /// this migration exists to stop, so the defence would fall to the same attack it is meant to block. The cost of this
    /// choice is that the floor follows the database machine's clock: winding that clock forward would let a delete
    /// through. That needs operating-system privilege on the server, which is a much higher bar than running one more
    /// INSERT, and a clock wound backwards only ever refuses more.
    /// </para>
    /// <para>
    /// Tests that fake the application clock are unaffected: <see cref="ControlServerDbContext.AuditClock"/> is read by
    /// the EF-layer guard only, and the governance suites build their databases with <c>EnsureCreatedAsync</c>, which
    /// runs no migration and therefore creates no trigger. <c>AuditDatabaseImmutabilityTests</c> runs the purge scenario
    /// on a migrated database to pin that the two clocks do not fight, and
    /// <c>TheFloorSitsExactlyAtOneHundredAndEightyDaysNotTwoHoursEitherSideOfIt</c> pins the boundary itself.
    /// </para>
    /// <para>
    /// <b>If these triggers ever go missing, a test says so -- but it will not say why.</b> EF Core's SQLite provider
    /// implements <c>DropColumn</c> and <c>AlterColumn</c> by rebuilding the table (create a temp table, copy, drop the
    /// original, rename), and a rebuild takes the table's triggers with it. So any later migration that changes a column
    /// on either audit table silently drops all six. That is caught, not silent:
    /// <c>AuditDatabaseImmutabilityTests.BothAuditTablesGetTheirThreeTriggersAndNoOtherTableGetsOne</c> goes red.
    /// <b>The fix is to re-create the triggers in that new migration, not to change the test.</b>
    /// </para>
    /// </remarks>
    public partial class AuditImmutabilityTriggers : Migration
    {
        /// <summary>Julian day number of 0001-01-01T00:00:00Z, the .NET tick epoch.</summary>
        private const string TickEpochJulianDay = "1721425.5";

        /// <summary>Ticks in one day: 24 * 60 * 60 * 10,000,000.</summary>
        private const string TicksPerDay = "864000000000";

        /// <summary>The REQ-0271 retention floor in ticks: 180 * 864000000000.</summary>
        private const string RetentionFloorTicks = "155520000000000";

        private static readonly string[] AuditTables = ["BusinessAuditRecords", "AdministratorAuditRecords"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (string table in AuditTables)
            {
                migrationBuilder.Sql(
                    $"""
                     CREATE TRIGGER "TR_{table}_NoUpdate"
                     BEFORE UPDATE ON "{table}"
                     FOR EACH ROW
                     BEGIN
                         SELECT RAISE(ABORT, '{table} is write-once: an audit record cannot be updated.');
                     END;
                     """);
                // REPLACE INTO / INSERT OR REPLACE is an INSERT, so NoUpdate never sees it; and the implicit delete it
                // does to resolve the primary-key conflict does not fire a BEFORE DELETE trigger while
                // PRAGMA recursive_triggers is OFF -- SQLite's default, and what this server runs with. Without this
                // third trigger a raw REPLACE rewrites an audit record of any age, whole (review S2).
                migrationBuilder.Sql(
                    $"""
                     CREATE TRIGGER "TR_{table}_NoReplace"
                     BEFORE INSERT ON "{table}"
                     FOR EACH ROW
                     WHEN EXISTS (SELECT 1 FROM "{table}" WHERE "AuditRecordId" = NEW."AuditRecordId")
                     BEGIN
                         SELECT RAISE(ABORT, '{table} is write-once: an existing audit record cannot be replaced.');
                     END;
                     """);
                migrationBuilder.Sql(
                    $"""
                     CREATE TRIGGER "TR_{table}_NoDeleteWithinRetentionFloor"
                     BEFORE DELETE ON "{table}"
                     FOR EACH ROW
                     WHEN OLD."RecordedAtUtcTicks" >
                         CAST((julianday('now') - {TickEpochJulianDay}) * {TicksPerDay} AS INTEGER) - {RetentionFloorTicks}
                     BEGIN
                         SELECT RAISE(ABORT, '{table} is write-once: a record inside the 180 day retention floor cannot be deleted.');
                     END;
                     """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only the six triggers. Nothing else in this migration touches a table, a column or an index.
            //
            // IF EXISTS, because a trigger can be gone before Down() runs and the way there is not exotic: EF Core's
            // SQLite provider implements DropColumn and AlterColumn by rebuilding the table (create temp, copy, drop
            // original, rename), and a rebuild takes the table's triggers with it. Any later ticket that changes a
            // column on either audit table therefore drops these; rolling back past this migration afterwards would
            // fail on "no such trigger" -- at the worst possible moment, because a rollback is the path taken when
            // something has already gone wrong.
            foreach (string table in AuditTables)
            {
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS \"TR_{table}_NoDeleteWithinRetentionFloor\";");
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS \"TR_{table}_NoReplace\";");
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS \"TR_{table}_NoUpdate\";");
            }
        }
    }
}
