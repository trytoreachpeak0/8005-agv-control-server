using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF migration generator emits inline metadata arrays.

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class JourneyDemandsRedispatchIndex : Migration
    {
        /// <summary>
        /// The index created by hand on the field database on 2026-11-05 (control-server#881) over the same two columns. Redundant
        /// once the official one exists, so it is dropped where present; a database without it is left as it is.
        /// </summary>
        private const string FieldTemporaryIndex = "IX_tmp_redispatch_JourneyDemands_DemandId_Generation";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every membership of a demand by generation, removed ones included (control-server#881): the redispatch read's
            // two sub-queries the partial IX_JourneyDemands_DemandId cannot answer. Only the index: no column, no row is touched.
            migrationBuilder.CreateIndex(
                name: "IX_JourneyDemands_DemandId_DispatchGeneration",
                table: "JourneyDemands",
                columns: new[] { "DemandId", "DispatchGeneration" });
            migrationBuilder.Sql($"DROP INDEX IF EXISTS \"{FieldTemporaryIndex}\";");
        }

        /// <inheritdoc />
        /// <remarks>Only the official index goes; the field's temporary one is not recreated.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JourneyDemands_DemandId_DispatchGeneration",
                table: "JourneyDemands");
        }
    }
}
