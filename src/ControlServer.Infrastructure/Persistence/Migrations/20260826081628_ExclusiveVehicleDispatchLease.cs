using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExclusiveVehicleDispatchLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VehicleDispatchLeases",
                columns: table => new
                {
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    VehicleKey = table.Column<string>(type: "TEXT", nullable: false),
                    AcquiredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleDispatchLeases", x => x.DemandId);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO VehicleDispatchLeases (DemandId, VehicleKey, AcquiredAt, ReleasedAt)
                SELECT accepted.DemandId,
                       pickup.VehicleKey,
                       accepted.AcceptedAt,
                       CASE
                           WHEN accepted.Status = 'Succeeded'
                               THEN COALESCE(completion.CompletedAt, accepted.AcceptedAt)
                           ELSE NULL
                       END
                FROM AcceptedDemands AS accepted
                INNER JOIN OrderIntents AS pickup
                    ON pickup.DemandId = accepted.DemandId
                   AND pickup.Purpose = 'TO_PICKUP'
                LEFT JOIN TransportDemandCompletions AS completion
                    ON completion.DemandId = accepted.DemandId;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleDispatchLeases_VehicleKey",
                table: "VehicleDispatchLeases",
                column: "VehicleKey",
                unique: true,
                filter: "ReleasedAt IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VehicleDispatchLeases");
        }
    }
}
