using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProtocolInboxReadIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ReceivedAtUtcTicks",
                table: "ProtocolInbox",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // Existing rows, to the tick, from the text EF stored: 'yyyy-MM-dd HH:mm:ss.FFFFFFFzzz', where the fraction and its
            // point are left out when they are zero. The whole seconds come from strftime('%s') on the text with the fraction cut
            // out, which applies the offset; the fraction is added back as digits, padded to seven. The fraction is cut out rather
            // than left to SQLite's date functions because they keep milliseconds only, and how a longer fraction is rounded is
            // theirs to decide (julianday rounds .9999999 up into the next second, strftime('%s') of the same text does not, on the
            // builds tried); cut out, the whole seconds are exact whatever the build does. The MVP's ef5dad8e backfilled to the
            // whole second instead; that moves a stored row up to a second earlier, which changes what the liveness and evidence
            // windows decide for the rows at their edge (control-server#563).
            migrationBuilder.Sql("""
                UPDATE "ProtocolInbox"
                SET "ReceivedAtUtcTicks" =
                    CAST(strftime('%s', substr("ReceivedAt", 1, 19) || substr("ReceivedAt", -6)) AS INTEGER) * 10000000
                    + CASE WHEN substr("ReceivedAt", 20, 1) = '.'
                           THEN CAST(substr(substr("ReceivedAt", 21, length("ReceivedAt") - 26) || '000000', 1, 7) AS INTEGER)
                           ELSE 0 END
                    + 621355968000000000;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ProtocolInbox_MessageType",
                table: "ProtocolInbox",
                column: "MessageType");

            migrationBuilder.CreateIndex(
                name: "IX_ProtocolInbox_ReceivedAtUtcTicks",
                table: "ProtocolInbox",
                column: "ReceivedAtUtcTicks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProtocolInbox_MessageType",
                table: "ProtocolInbox");

            migrationBuilder.DropIndex(
                name: "IX_ProtocolInbox_ReceivedAtUtcTicks",
                table: "ProtocolInbox");

            // Not DropColumn: on SQLite EF rebuilds the whole table for it, from the previous model, and that rebuild drops
            // the empty-string default MessageType was given when it was added -- a down-and-up round trip then no longer
            // leaves the table as it found it. SQLite drops an unindexed column in place, keeping every other definition
            // and the column order; the two indexes are dropped above first because it refuses an indexed one.
            migrationBuilder.Sql("""
                ALTER TABLE "ProtocolInbox" DROP COLUMN "ReceivedAtUtcTicks";
                """);
        }
    }
}
