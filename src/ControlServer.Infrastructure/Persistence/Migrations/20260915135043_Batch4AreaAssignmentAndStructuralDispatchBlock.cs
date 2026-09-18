using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Batch4AreaAssignmentAndStructuralDispatchBlock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DispatchZoneAreaAssignments",
                columns: table => new
                {
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    Area = table.Column<string>(type: "TEXT", nullable: false),
                    DispatchZone = table.Column<string>(type: "TEXT", nullable: false),
                    SlotPosition = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchZoneAreaAssignments", x => new { x.Version, x.Area });
                });

            migrationBuilder.CreateTable(
                name: "DispatchZoneAreaAssignmentVersions",
                columns: table => new
                {
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotId = table.Column<string>(type: "TEXT", nullable: false),
                    EntryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchZoneAreaAssignmentVersions", x => x.Version);
                });

            migrationBuilder.CreateTable(
                name: "StructuralDispatchBlocks",
                columns: table => new
                {
                    DemandId = table.Column<string>(type: "TEXT", nullable: false),
                    ReasonCode = table.Column<string>(type: "TEXT", nullable: false),
                    TransportDemandKey = table.Column<string>(type: "TEXT", nullable: false),
                    FirstRaisedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ClearedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DetailJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StructuralDispatchBlocks", x => new { x.DemandId, x.ReasonCode });
                });

            migrationBuilder.CreateIndex(
                name: "IX_DispatchZoneAreaAssignmentVersions_SnapshotId",
                table: "DispatchZoneAreaAssignmentVersions",
                column: "SnapshotId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StructuralDispatchBlocks_ClearedAt",
                table: "StructuralDispatchBlocks",
                column: "ClearedAt");

            migrationBuilder.CreateIndex(
                name: "IX_StructuralDispatchBlocks_TransportDemandKey",
                table: "StructuralDispatchBlocks",
                column: "TransportDemandKey");

            // 仓位分组取值改名：LEFT -> FRONT，RIGHT -> REAR（REQ-0349，program#70 定案 4）。
            //
            // 这是同一物理事实的术语更名，不是硬件变化：1～4 号仍是同一侧仓门，5～8 号仍是另一侧。所以只改
            // SlotModelSlots 的当前值；历史快照与审计保留原文——GovernedConfigurationSnapshots 的 ContentJson、
            // ContentSha256 与业务、管理员审计记录一律不动，它们定义为不可改写（REQ-0271），连快照一起改写并重算
            // 摘要违背这一点。也不发布整车模型第 2 版：那在分类上是整车维护，要逐车重新核验。
            //
            // 两条语句只匹配旧词，重跑不会改动任何已是 FRONT／REAR 或其它取值的行。
            migrationBuilder.Sql("UPDATE SlotModelSlots SET SlotPosition = 'FRONT' WHERE SlotPosition = 'LEFT';");
            migrationBuilder.Sql("UPDATE SlotModelSlots SET SlotPosition = 'REAR' WHERE SlotPosition = 'RIGHT';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 回到批次 3 的词汇：那一版代码读写的是 LEFT／RIGHT。
            migrationBuilder.Sql("UPDATE SlotModelSlots SET SlotPosition = 'LEFT' WHERE SlotPosition = 'FRONT';");
            migrationBuilder.Sql("UPDATE SlotModelSlots SET SlotPosition = 'RIGHT' WHERE SlotPosition = 'REAR';");

            migrationBuilder.DropTable(
                name: "DispatchZoneAreaAssignments");

            migrationBuilder.DropTable(
                name: "DispatchZoneAreaAssignmentVersions");

            migrationBuilder.DropTable(
                name: "StructuralDispatchBlocks");
        }
    }
}
