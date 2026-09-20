using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlServer.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// control-server#211：把升级那一刻正在装货的那条归属回填成 <c>LOADING</c>。**只改数据，不动任何 schema。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么需要。</b>「这个停靠此刻在装哪一条需求」在本票之前不是落库的状态，本票把它变成了归属行上的
    /// <c>LOADING</c>。<c>JourneyRuntimeEngine</c> 在 <c>AwaitingLoadResult</c> 阶段按这个状态找那条需求，找不到
    /// 就抛——那个 <c>throw</c> 是新立的不变量「命令与状态要么都在、要么都不在」的护栏，有意为之。
    /// 于是跨过本提交升级时，一条正停在 <c>AwaitingLoadResult</c> 的旅程，它的归属行还是老值
    /// <c>PENDING_LOAD</c>，新代码每一轮都抛，旅程永久卡住，要人工介入。
    /// </para>
    /// <para>
    /// <b>为什么是迁移而不是读取侧兜底。</b>兜底会永远留在代码里，而且会弱化上面那条不变量：真出现「命令发了、
    /// 状态没写」的缺陷时，本该抛的地方会安静地挑一条继续跑——在它最该出声的时候沉默。迁移是一次性的，跑完不再
    /// 参与运行逻辑，护栏原样留着。批次7-06 票面写的是「零 migration」，这一条是 Coordinator 7 在 2026-09-20
    /// 明确松开那条约束后加的，**只限本条、只改数据**。
    /// </para>
    /// <para>
    /// <b>谓词为什么无歧义。</b>多停靠、以及「一个停靠上挂多条需求」都是本票引入的；跨过本提交的老数据里，
    /// 一趟旅程只有一条需求（批次 7 建表票 control-server#206 的回填就是按「一条需求、一个取货停靠、一个卸货
    /// 停靠」写的）。所以「这趟旅程里那条 <c>PENDING_LOAD</c>」没有第二个候选，不需要再按停靠去认，也不用猜。
    /// </para>
    /// <para>
    /// <b>条件不存在时它什么都不做</b>：没有旅程停在 <c>AwaitingLoadResult</c>，就一行都不匹配。v2 今天还没上
    /// 生产（生产跑的是 MVP 线），所以这条迁移在生产库上大概率是空跑——它存在是为了不依赖「某个人在正确的时刻
    /// 记得做正确的事」。
    /// </para>
    /// </remarks>
    public partial class Batch7LoadingMembershipBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            migrationBuilder.Sql(
                """
                UPDATE "JourneyDemands"
                SET "Status" = 'LOADING'
                WHERE "Status" = 'PENDING_LOAD'
                  AND "RemovedAt" IS NULL
                  AND "JourneyId" IN (
                      SELECT "JourneyId" FROM "JourneyRuntimes" WHERE "Stage" = 'AwaitingLoadResult')
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);
            // 回滚到本提交之前的代码：那一版不认识 LOADING，按「这个停靠上第一条还没装的」找需求，
            // 所以这条归属要退回 PENDING_LOAD。反向同样无歧义——LOADING 至多一条。
            migrationBuilder.Sql(
                """
                UPDATE "JourneyDemands"
                SET "Status" = 'PENDING_LOAD'
                WHERE "Status" = 'LOADING'
                  AND "RemovedAt" IS NULL
                """);
        }
    }
}
