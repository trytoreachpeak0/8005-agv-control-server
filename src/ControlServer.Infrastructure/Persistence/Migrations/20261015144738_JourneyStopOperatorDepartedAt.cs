using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class JourneyStopOperatorDepartedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OperatorDepartedAt",
                table: "JourneyStops",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OperatorDepartedAt",
                table: "JourneyStops");
        }
    }
}
