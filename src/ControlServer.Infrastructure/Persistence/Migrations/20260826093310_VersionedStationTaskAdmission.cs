using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VersionedStationTaskAdmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdmissionDecisionSnapshots",
                columns: table => new
                {
                    SlotOperationAttemptId = table.Column<string>(type: "TEXT", nullable: false),
                    StationId = table.Column<string>(type: "TEXT", nullable: false),
                    TaskType = table.Column<string>(type: "TEXT", nullable: false),
                    AdmissionPolicyVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    AdmittedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Allowed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdmissionDecisionSnapshots", x => x.SlotOperationAttemptId);
                });

            migrationBuilder.CreateTable(
                name: "AdmissionPolicyAudit",
                columns: table => new
                {
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    DeploymentId = table.Column<string>(type: "TEXT", nullable: false),
                    PreviousContentHash = table.Column<string>(type: "TEXT", nullable: true),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    RelationsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdmissionPolicyAudit", x => x.Version);
                });

            migrationBuilder.CreateTable(
                name: "AdmissionPolicyState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    DeploymentId = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdmissionPolicyState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StationTaskTypeAdmissions",
                columns: table => new
                {
                    StationId = table.Column<string>(type: "TEXT", nullable: false),
                    TaskType = table.Column<string>(type: "TEXT", nullable: false),
                    PolicyVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StationTaskTypeAdmissions", x => new { x.StationId, x.TaskType });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdmissionDecisionSnapshots");

            migrationBuilder.DropTable(
                name: "AdmissionPolicyAudit");

            migrationBuilder.DropTable(
                name: "AdmissionPolicyState");

            migrationBuilder.DropTable(
                name: "StationTaskTypeAdmissions");
        }
    }
}
