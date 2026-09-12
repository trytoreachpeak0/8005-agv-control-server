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

            // Existing rows, to the whole second: strftime('%s') converts the stored offset to UTC and
            // drops the fraction, so no row reads as more recent than it was received. It only matters to
            // the liveness window in the first MaximumEvidenceAge after migrating; later rows carry exact
            // ticks from ProtocolInboxRow.ReceivedAt.
            migrationBuilder.Sql("""
                UPDATE "ProtocolInbox"
                SET "ReceivedAtUtcTicks" = CAST(strftime('%s', "ReceivedAt") AS INTEGER) * 10000000 + 621355968000000000;
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

            migrationBuilder.DropColumn(
                name: "ReceivedAtUtcTicks",
                table: "ProtocolInbox");
        }
    }
}
