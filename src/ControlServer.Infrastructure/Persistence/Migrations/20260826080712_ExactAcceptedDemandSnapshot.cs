using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExactAcceptedDemandSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "AcceptedDemands",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<int>(
                name: "Generation",
                table: "AcceptedDemands",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LiveMesFieldsJson",
                table: "AcceptedDemands",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SeriesId",
                table: "AcceptedDemands",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Sublot",
                table: "AcceptedDemands",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ValueObservedAt",
                table: "AcceptedDemands",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "ValuePollTraceId",
                table: "AcceptedDemands",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ValueProjectionCommitId",
                table: "AcceptedDemands",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "WorkType",
                table: "AcceptedDemands",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "AcceptedDemands");

            migrationBuilder.DropColumn(
                name: "Generation",
                table: "AcceptedDemands");

            migrationBuilder.DropColumn(
                name: "LiveMesFieldsJson",
                table: "AcceptedDemands");

            migrationBuilder.DropColumn(
                name: "SeriesId",
                table: "AcceptedDemands");

            migrationBuilder.DropColumn(
                name: "Sublot",
                table: "AcceptedDemands");

            migrationBuilder.DropColumn(
                name: "ValueObservedAt",
                table: "AcceptedDemands");

            migrationBuilder.DropColumn(
                name: "ValuePollTraceId",
                table: "AcceptedDemands");

            migrationBuilder.DropColumn(
                name: "ValueProjectionCommitId",
                table: "AcceptedDemands");

            migrationBuilder.DropColumn(
                name: "WorkType",
                table: "AcceptedDemands");
        }
    }
}
