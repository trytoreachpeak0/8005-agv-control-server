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
