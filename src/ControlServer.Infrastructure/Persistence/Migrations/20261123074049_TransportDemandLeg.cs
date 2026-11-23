using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF migration generator emits inline metadata arrays.

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 需求身份加「程」（control-server#911）：<c>AcceptedDemands</c>、<c>TransportDemandSuppressions</c>、<c>TransportDemandCompletions</c>
    /// 各加一列 <c>TransportLeg</c>（非空，缺省空串），受理唯一索引与两张表的主键从「业务键」改成「业务键 + 程」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>回填</b>（<see cref="BackfillLegs"/>）：受理行里 <c>STAGING_TO_WIRE</c> 的程取它受理时冻结的 <c>LiveMesFieldsJson</c> 里的
    /// <c>Step</c>（空白则为空串，与 <c>TransportDemandIdentity.LegOf</c> 一致），其余任务类型留空串；抑制行与完成行按自己的
    /// <c>DemandId</c> 取那条受理行的程。新主键与新唯一索引都比旧的宽（多一列），所以无论回填先后都不会撞键。
    /// 分程的任务类型在这里写死成字面值，不引用代码里的清单：迁移是历史，清单以后变了，这一步也不能跟着变。
    /// </para>
    /// <para>
    /// <b>降级</b>（<see cref="CollapseLegsForRollback"/>）：旧版本按业务键唯一。同一业务键下有不止一程时，最早写下的那一行（rowid 最小）
    /// 保留原键，其余各行的业务键改成 <c>{key}#{leg}</c>，旧版本按键再也查不到它们——回到本票之前的行为：第二程又被第一程挡住，
    /// 方向是少派、不是重派。改过名的行只留作历史；降级时不要有焊线2 那程在途。
    /// </para>
    /// </remarks>
    public partial class TransportDemandLeg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_TransportDemandSuppressions",
                table: "TransportDemandSuppressions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_TransportDemandCompletions",
                table: "TransportDemandCompletions");

            migrationBuilder.DropIndex(
                name: "IX_AcceptedDemands_TransportDemandKey",
                table: "AcceptedDemands");

            migrationBuilder.AddColumn<string>(
                name: "TransportLeg",
                table: "TransportDemandSuppressions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TransportLeg",
                table: "TransportDemandCompletions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TransportLeg",
                table: "AcceptedDemands",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_TransportDemandSuppressions",
                table: "TransportDemandSuppressions",
                columns: new[] { "TransportDemandKey", "TransportLeg" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_TransportDemandCompletions",
                table: "TransportDemandCompletions",
                columns: new[] { "TransportDemandKey", "TransportLeg" });

            migrationBuilder.CreateIndex(
                name: "IX_AcceptedDemands_TransportDemandKey_TransportLeg",
                table: "AcceptedDemands",
                columns: new[] { "TransportDemandKey", "TransportLeg" },
                unique: true);

            BackfillLegs(migrationBuilder);
        }

        /// <inheritdoc />
        /// <remarks>
        /// <c>AcceptedDemands</c> 的列用 SQLite 原地 <c>DROP COLUMN</c> 删，不经 EF 的整表重建：重建按上一版模型建表，那份模型没有
        /// 早年 <c>ADD COLUMN</c> 留下的缺省值，会把 <c>CreatedAt</c> 等列的 <c>DEFAULT</c> 悄悄改掉（批次 7 迁移纪律测试的往返判据看得见）。
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            CollapseLegsForRollback(migrationBuilder);

            migrationBuilder.DropPrimaryKey(
                name: "PK_TransportDemandSuppressions",
                table: "TransportDemandSuppressions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_TransportDemandCompletions",
                table: "TransportDemandCompletions");

            migrationBuilder.DropIndex(
                name: "IX_AcceptedDemands_TransportDemandKey_TransportLeg",
                table: "AcceptedDemands");

            migrationBuilder.DropColumn(
                name: "TransportLeg",
                table: "TransportDemandSuppressions");

            migrationBuilder.DropColumn(
                name: "TransportLeg",
                table: "TransportDemandCompletions");

            // Not migrationBuilder.DropColumn: on SQLite EF drops a column by rebuilding the table from the previous model,
            // and that model does not carry the DEFAULTs earlier ADD COLUMNs gave AcceptedDemands (CreatedAt and others),
            // so a rebuild would quietly change those columns. SQLite drops an unindexed column in place.
            migrationBuilder.Sql("""ALTER TABLE "AcceptedDemands" DROP COLUMN "TransportLeg";""");

            migrationBuilder.AddPrimaryKey(
                name: "PK_TransportDemandSuppressions",
                table: "TransportDemandSuppressions",
                column: "TransportDemandKey");

            migrationBuilder.AddPrimaryKey(
                name: "PK_TransportDemandCompletions",
                table: "TransportDemandCompletions",
                column: "TransportDemandKey");

            migrationBuilder.CreateIndex(
                name: "IX_AcceptedDemands_TransportDemandKey",
                table: "AcceptedDemands",
                column: "TransportDemandKey",
                unique: true);
        }

        private static void BackfillLegs(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "AcceptedDemands"
                SET "TransportLeg" = CASE
                    WHEN trim(coalesce(json_extract("LiveMesFieldsJson", '$.Step'), ''), ' ' || char(9, 10, 11, 12, 13)) = '' THEN ''
                    ELSE json_extract("LiveMesFieldsJson", '$.Step')
                END
                WHERE "WorkType" = 'STAGING_TO_WIRE'
                   OR ("WorkType" = '' AND "TransportDemandKey" LIKE '%|STAGING_TO_WIRE');
                """);
            migrationBuilder.Sql(
                """
                UPDATE "TransportDemandSuppressions"
                SET "TransportLeg" = (
                    SELECT "a"."TransportLeg" FROM "AcceptedDemands" AS "a"
                    WHERE "a"."DemandId" = "TransportDemandSuppressions"."DemandId"
                      AND "a"."TransportDemandKey" = "TransportDemandSuppressions"."TransportDemandKey")
                WHERE EXISTS (
                    SELECT 1 FROM "AcceptedDemands" AS "a"
                    WHERE "a"."DemandId" = "TransportDemandSuppressions"."DemandId"
                      AND "a"."TransportDemandKey" = "TransportDemandSuppressions"."TransportDemandKey");
                """);
            migrationBuilder.Sql(
                """
                UPDATE "TransportDemandCompletions"
                SET "TransportLeg" = (
                    SELECT "a"."TransportLeg" FROM "AcceptedDemands" AS "a"
                    WHERE "a"."DemandId" = "TransportDemandCompletions"."DemandId"
                      AND "a"."TransportDemandKey" = "TransportDemandCompletions"."TransportDemandKey")
                WHERE EXISTS (
                    SELECT 1 FROM "AcceptedDemands" AS "a"
                    WHERE "a"."DemandId" = "TransportDemandCompletions"."DemandId"
                      AND "a"."TransportDemandKey" = "TransportDemandCompletions"."TransportDemandKey");
                """);
        }

        private static void CollapseLegsForRollback(MigrationBuilder migrationBuilder)
        {
            foreach (string table in new[] { "AcceptedDemands", "TransportDemandSuppressions", "TransportDemandCompletions" })
            {
                migrationBuilder.Sql(
                    $"""
                    UPDATE "{table}"
                    SET "TransportDemandKey" = "TransportDemandKey" || '#' || "TransportLeg"
                    WHERE EXISTS (
                        SELECT 1 FROM "{table}" AS "earlier"
                        WHERE "earlier"."TransportDemandKey" = "{table}"."TransportDemandKey"
                          AND "earlier"."rowid" < "{table}"."rowid");
                    """);
            }
        }
    }
}
