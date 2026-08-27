using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional
#pragma warning disable CA1861 // EF migration generator emits inline metadata arrays.

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PackageCapacityAndSafetyProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MissingPackages",
                columns: table => new
                {
                    Package = table.Column<string>(type: "TEXT", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MissingPackages", x => x.Package);
                });

            migrationBuilder.CreateTable(
                name: "PackageCapacityRules",
                columns: table => new
                {
                    RuleId = table.Column<string>(type: "TEXT", nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", nullable: false),
                    MatchType = table.Column<string>(type: "TEXT", nullable: false),
                    MaxBoxesPerBasket = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    EffectiveAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SupersededAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageCapacityRules", x => x.RuleId);
                });

            migrationBuilder.InsertData(
                table: "PackageCapacityRules",
                columns: new[] { "RuleId", "EffectiveAt", "MatchType", "MaxBoxesPerBasket", "Pattern", "Source", "SupersededAt", "Version" },
                values: new object[,]
                {
                    { "v1:exact:PDFN5×6-8L(12R)", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 4, "PDFN5×6-8L(12R)", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:PDFNWB3.3×3.34", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 4, "PDFNWB3.3×3.34", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:PDFNWB5×6", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 4, "PDFNWB5×6", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-126", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 8, "TO-126", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-220-2L-C", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 8, "TO-220-2L-C", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-220-3L", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 8, "TO-220-3L", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-220-3L-C(T0.5mm)", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 8, "TO-220-3L-C(T0.5mm)", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-220-5L", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 8, "TO-220-5L", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-220D-5L", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 8, "TO-220D-5L", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-220F", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 8, "TO-220F", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-247", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 6, "TO-247", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-247-2L-A", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 6, "TO-247-2L-A", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-247-4L", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 6, "TO-247-4L", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-247A-4L", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 6, "TO-247A-4L", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-247B-3L", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 6, "TO-247B-3L", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-247Plus-4L", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 6, "TO-247Plus-4L", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-251", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 10, "TO-251", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-252-2L(4R)", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 5, "TO-252-2L(4R)", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-252-2L(6R)", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 4, "TO-252-2L(6R)", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-252-2L(8R)", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 4, "TO-252-2L(8R)", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-252-5L", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 10, "TO-252-5L", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-263-2L", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 8, "TO-263-2L", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-263-5L", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 8, "TO-263-5L", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-263-7L(2R)", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 6, "TO-263-7L(2R)", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-263C-2L", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 8, "TO-263C-2L", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-264-3L", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 8, "TO-264-3L", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:exact:TO-92", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "exact", 12, "TO-92", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 },
                    { "v1:prefix:TOLL-", new DateTimeOffset(new DateTime(2026, 7, 16, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 8, 0, 0, 0)), "prefix", 3, "TOLL-", "客户提供花篮容量对照表（2026-07-16迁移）", null, 1 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_PackageCapacityRules_Pattern_MatchType",
                table: "PackageCapacityRules",
                columns: new[] { "Pattern", "MatchType" },
                unique: true,
                filter: "SupersededAt IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MissingPackages");

            migrationBuilder.DropTable(
                name: "PackageCapacityRules");
        }
    }
}
