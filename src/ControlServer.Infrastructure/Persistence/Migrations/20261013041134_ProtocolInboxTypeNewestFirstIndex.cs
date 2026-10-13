using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF migration generator emits inline metadata arrays.

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProtocolInboxTypeNewestFirstIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One message type newest first without sorting the whole type first (control-server#597). Only the index: no
            // column, no row is touched. On an existing inbox it is built when the Host starts and runs MigrateAsync.
            migrationBuilder.CreateIndex(
                name: "IX_ProtocolInbox_MessageType_ReceivedAtUtcTicks",
                table: "ProtocolInbox",
                columns: new[] { "MessageType", "ReceivedAtUtcTicks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProtocolInbox_MessageType_ReceivedAtUtcTicks",
                table: "ProtocolInbox");
        }
    }
}
