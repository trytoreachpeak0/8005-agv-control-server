using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VehicleFaultOriginSymptomCode : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// The symptom a fault generation began on (control-server#897). Nullable and not backfilled: a row written before it is
        /// judged on its evidence code, as before; the next generation on that vehicle writes it.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OriginSymptomCode",
                table: "VehicleFaultStates",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OriginSymptomCode",
                table: "VehicleFaultStates");
        }
    }
}
