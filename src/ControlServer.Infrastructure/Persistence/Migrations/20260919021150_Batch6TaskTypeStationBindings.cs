using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // EF migration generator emits inline metadata arrays.

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 批次 6 唯一建表票 control-server#159：任务类型规则、按图绑定集与生效指针、按 Map + TASK_TYPE 的暂停、目录变化记录。
    /// 只建新表，不动任何既有表；需求冻结复用批次 3 的 ConfigurationConsumerBindings，不给 JourneyRuntimes 加列。
    /// </summary>
    public partial class Batch6TaskTypeStationBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskTypeStationActiveBindingSets",
                columns: table => new
                {
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    ActiveVersion = table.Column<long>(type: "INTEGER", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    PendingVersion = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskTypeStationActiveBindingSets", x => x.MapId);
                });

            migrationBuilder.CreateTable(
                name: "TaskTypeStationBindings",
                columns: table => new
                {
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    TaskType = table.Column<string>(type: "TEXT", nullable: false),
                    StationRiotId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationName = table.Column<string>(type: "TEXT", nullable: false),
                    SiteVerificationRef = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskTypeStationBindings", x => new { x.MapId, x.Version, x.TaskType });
                });

            migrationBuilder.CreateTable(
                name: "TaskTypeStationBindingSetVersions",
                columns: table => new
                {
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    RuleVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<string>(type: "TEXT", nullable: false),
                    CatalogRevision = table.Column<long>(type: "INTEGER", nullable: true),
                    LoadedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskTypeStationBindingSetVersions", x => new { x.MapId, x.Version });
                });

            migrationBuilder.CreateTable(
                name: "TaskTypeStationCatalogChanges",
                columns: table => new
                {
                    ChangeId = table.Column<string>(type: "TEXT", nullable: false),
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    StationRiotId = table.Column<int>(type: "INTEGER", nullable: false),
                    PreviousStationName = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentStationName = table.Column<string>(type: "TEXT", nullable: true),
                    ChangeKind = table.Column<string>(type: "TEXT", nullable: false),
                    RiskClass = table.Column<string>(type: "TEXT", nullable: false),
                    CatalogRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AffectedTaskTypesJson = table.Column<string>(type: "TEXT", nullable: false),
                    HoldId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskTypeStationCatalogChanges", x => x.ChangeId);
                });

            migrationBuilder.CreateTable(
                name: "TaskTypeStationHolds",
                columns: table => new
                {
                    HoldId = table.Column<string>(type: "TEXT", nullable: false),
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    TaskType = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    ReasonCode = table.Column<string>(type: "TEXT", nullable: false),
                    DetailJson = table.Column<string>(type: "TEXT", nullable: false),
                    RaisedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RaisedBy = table.Column<string>(type: "TEXT", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ReleasedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskTypeStationHolds", x => x.HoldId);
                });

            migrationBuilder.CreateTable(
                name: "TaskTypeStationRequirements",
                columns: table => new
                {
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    TaskType = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskTypeStationRequirements", x => new { x.MapId, x.Version, x.TaskType });
                });

            migrationBuilder.CreateTable(
                name: "TaskTypeStationRules",
                columns: table => new
                {
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    TaskType = table.Column<string>(type: "TEXT", nullable: false),
                    FixedEnd = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskTypeStationRules", x => new { x.Version, x.TaskType });
                });

            migrationBuilder.CreateTable(
                name: "TaskTypeStationRuleVersions",
                columns: table => new
                {
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<string>(type: "TEXT", nullable: false),
                    LoadedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskTypeStationRuleVersions", x => x.Version);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskTypeStationBindings_MapId_Version_StationRiotId",
                table: "TaskTypeStationBindings",
                columns: new[] { "MapId", "Version", "StationRiotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskTypeStationBindingSetVersions_SnapshotId",
                table: "TaskTypeStationBindingSetVersions",
                column: "SnapshotId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskTypeStationCatalogChanges_MapId_StationRiotId_CatalogRevision",
                table: "TaskTypeStationCatalogChanges",
                columns: new[] { "MapId", "StationRiotId", "CatalogRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskTypeStationHolds_MapId_TaskType_ReleasedAt",
                table: "TaskTypeStationHolds",
                columns: new[] { "MapId", "TaskType", "ReleasedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskTypeStationRuleVersions_SnapshotId",
                table: "TaskTypeStationRuleVersions",
                column: "SnapshotId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskTypeStationActiveBindingSets");

            migrationBuilder.DropTable(
                name: "TaskTypeStationBindings");

            migrationBuilder.DropTable(
                name: "TaskTypeStationBindingSetVersions");

            migrationBuilder.DropTable(
                name: "TaskTypeStationCatalogChanges");

            migrationBuilder.DropTable(
                name: "TaskTypeStationHolds");

            migrationBuilder.DropTable(
                name: "TaskTypeStationRequirements");

            migrationBuilder.DropTable(
                name: "TaskTypeStationRules");

            migrationBuilder.DropTable(
                name: "TaskTypeStationRuleVersions");
        }
    }
}
