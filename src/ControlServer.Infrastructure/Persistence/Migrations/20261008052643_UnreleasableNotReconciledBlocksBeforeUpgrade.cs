using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// control-server#505：升级那一刻停在 <c>Blocked</c>、阻塞码是 <c>*_NOT_RECONCILED</c> 的旅程，阻塞码改成不可放行的
    /// <c>*_NOT_RECONCILED_BEFORE_UPGRADE</c>。<b>只改数据，不动任何 schema</b>，所以模型快照不变，也不重建任何表。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么需要。</b>本提交起，两条放行路径对普通的 <c>*_NOT_RECONCILED</c> 不再一律不放，而是看旅程里还有没有
    /// <c>RecoveryRequired</c> 的需求——前提是每个这样的阻塞码都有一条这样的需求作标记。本提交之前的代码不保证这一点：需求已送达或已取消时
    /// 只写阻塞码、不留标记。升级前写下的行分不出是哪一种，若照新规则判，一条无标记的阻塞（例如已卸需求的纠错没对上）会随另一条需求的修复续行
    /// 或交接一起被放掉，那次没对上的仓位事实就丢了。
    /// </para>
    /// <para>
    /// <b>为什么全部改、不挑。</b>本提交之前第 5 条对所有 <c>*_NOT_RECONCILED</c> 一律不放，所以升级前库里这类行全都卡在 <c>Blocked</c>；全部改成
    /// 不可放行，现场行为一点不变，也不必判断哪一行有标记。它们仍只由旅程收尾结束，受治理的单方出口归 control-server#485。
    /// </para>
    /// <para>
    /// <b>为什么是迁移而不是启动时扫描。</b>扫描每次启动都跑，会把升级之后新写下的、带标记的阻塞也改掉：能不能放车取决于服务器重启过没有。
    /// 迁移只跑一次，只碰升级前的行。
    /// </para>
    /// <para>
    /// 判后缀用 <c>substr</c> 而不用 <c>LIKE</c>：<c>LIKE</c> 里的下划线是通配符，且不分大小写。<c>BlockReasonSince</c> 不动：旅程从那时起就卡着。
    /// </para>
    /// </remarks>
    public partial class UnreleasableNotReconciledBlocksBeforeUpgrade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.Sql(
                """
                UPDATE "JourneyRuntimes"
                SET "BlockReasonCode" = "BlockReasonCode" || '_BEFORE_UPGRADE'
                WHERE "Stage" = 'Blocked'
                  AND substr("BlockReasonCode", -15) = '_NOT_RECONCILED'
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            // 回滚到本提交之前的代码：那一版只认 *_NOT_RECONCILED（第 5 条一律不放），不认识两个新后缀——不认识的码它不挡，
            // 会照常放行。所以两个后缀都去掉，退回它会挡住的普通码：本迁移改过的行恢复原样，本提交之后新写下的
            // *_NOT_RECONCILED_ON_ENDED_DEMAND 也退回 *_NOT_RECONCILED。
            migrationBuilder.Sql(
                """
                UPDATE "JourneyRuntimes"
                SET "BlockReasonCode" = substr("BlockReasonCode", 1, length("BlockReasonCode") - 15)
                WHERE substr("BlockReasonCode", -30) = '_NOT_RECONCILED_BEFORE_UPGRADE'
                """);
            migrationBuilder.Sql(
                """
                UPDATE "JourneyRuntimes"
                SET "BlockReasonCode" = substr("BlockReasonCode", 1, length("BlockReasonCode") - 16)
                WHERE substr("BlockReasonCode", -31) = '_NOT_RECONCILED_ON_ENDED_DEMAND'
                """);
        }
    }
}
