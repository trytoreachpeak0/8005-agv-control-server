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
    /// opened straight onto the file all walk past it (control-server#161 review O1). These four triggers put the rule on
    /// the tables themselves, where every one of those paths has to meet it.
    /// </para>
    /// <para>
    /// <b>UPDATE is refused outright.</b> There is no retention question to ask: an audit record is never rewritten, at
    /// any age, for any reason.
    /// </para>
    /// <para>
    /// <b>DELETE is refused only inside the 180 day floor</b> (REQ-0271), so that
    /// <c>GovernanceStore.PurgeExpiredAuditAsync</c> keeps working. A trigger cannot read configuration, so it cannot know
    /// about a deployment that configured retention longer than the floor; that longer period stays the EF layer's to
    /// enforce. The division is deliberate: the database holds the line nobody may cross, the application holds the line
    /// this deployment drew for itself.
    /// </para>
    /// <para>
    /// <b>How the trigger gets "now".</b> From <c>julianday('now')</c> -- the database machine's clock -- converted to
    /// .NET ticks so it can be compared with <c>RecordedAtUtcTicks</c>:
    /// <c>(julianday('now') - 1721425.5) * 864000000000</c>, where 1721425.5 is the Julian day of 0001-01-01T00:00:00Z
    /// (the .NET tick epoch) and 864000000000 is the ticks in a day. The double loses about 128 ticks (13 microseconds)
    /// of precision at this magnitude, which is nothing against a 180 day boundary. The alternative -- refuse every
    /// delete and let the purge write an authorising row inside its own transaction -- was rejected: that row is itself
    /// writable by the raw SQL this migration exists to stop, so the defence would fall to the same attack it is meant to
    /// block. The cost of this choice is that the floor follows the database machine's clock: winding that clock forward
    /// would let a delete through. That needs operating-system privilege on the server, which is a much higher bar than
    /// running one more INSERT, and a clock wound backwards only ever refuses more.
    /// </para>
    /// <para>
    /// Tests that fake the application clock are unaffected: <see cref="ControlServerDbContext.AuditClock"/> is read by
    /// the EF-layer guard only, and the governance suites build their databases with <c>EnsureCreatedAsync</c>, which
    /// runs no migration and therefore creates no trigger. <c>AuditDatabaseImmutabilityTests</c> runs the purge scenario
    /// on a migrated database to pin that the two clocks do not fight.
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
            // Only the four triggers. Nothing else in this migration touches a table, a column or an index.
            foreach (string table in AuditTables)
            {
                migrationBuilder.Sql($"DROP TRIGGER \"TR_{table}_NoDeleteWithinRetentionFloor\";");
                migrationBuilder.Sql($"DROP TRIGGER \"TR_{table}_NoUpdate\";");
            }
        }
    }
}
