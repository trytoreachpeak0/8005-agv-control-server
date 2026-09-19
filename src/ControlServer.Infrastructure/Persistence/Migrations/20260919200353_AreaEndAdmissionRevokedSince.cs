using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AreaEndAdmissionRevokedSince : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AreaEndAdmissionRevokedSince",
                table: "JourneyRuntimes",
                type: "TEXT",
                nullable: true);

            // control-server#228: a stop already held at its AREA machine -- still waiting, or escalated past the threshold
            // -- keeps when its wait began. control-server#198 kept that on the block itself, from the first hold through the
            // escalation, so it is read from there. A journey whose hold was overwritten by another code has lost it; its
            // next hold starts the count.
            migrationBuilder.Sql(
                """
                UPDATE "JourneyRuntimes"
                SET "AreaEndAdmissionRevokedSince" = "BlockReasonSince"
                WHERE ("Stage" = 'AwaitingGateArrival' AND "BlockReasonCode" = 'TASK_TYPE_NOT_ALLOWED_AT_STATION')
                   OR ("Stage" = 'Blocked' AND "BlockReasonCode" = 'TASK_TYPE_NOT_ALLOWED_AT_STATION_TIMEOUT');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AreaEndAdmissionRevokedSince",
                table: "JourneyRuntimes");
        }
    }
}
