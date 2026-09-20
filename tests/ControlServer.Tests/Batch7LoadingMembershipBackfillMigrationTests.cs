using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ControlServer.Tests;

/// <summary>
/// 升级那一刻正在装货的那条归属被回填成 <c>LOADING</c>（批次7-06，control-server#211）。
/// </summary>
/// <remarks>
/// <para>
/// 「这个停靠此刻在装哪一条需求」在本票之前不是落库的状态。本票把它变成归属行上的 <c>LOADING</c>，而
/// <c>JourneyRuntimeEngine</c> 在 <c>AwaitingLoadResult</c> 阶段找不到它就抛——那个 <c>throw</c> 是新立的不变量
/// 「命令与状态要么都在、要么都不在」的护栏，有意为之，不改。所以跨过本提交升级时，一条正停在
/// <c>AwaitingLoadResult</c> 的旅程会每一轮都抛、永久卡住，要人工介入。
/// </para>
/// <para>
/// <b>判据不是「有行被改了」，是「该改的改了、不该改的一个都没动」。</b>只断言目标那条变成 <c>LOADING</c>，
/// 一条把全表刷成 <c>LOADING</c> 的迁移也能通过——而那会让一条还没开始装的需求被当成正在装。
/// </para>
/// </remarks>
public sealed class Batch7LoadingMembershipBackfillMigrationTests
{
    private const string PreviousMigration = "20260920001500_AuditImmutabilityTriggers";

    /// <summary>正在装货的那一条，升级后是 <c>LOADING</c>。</summary>
    private const string LoadingDemandId = "D-LOADING";

    /// <summary>车还在去取货站的路上，一条命令都没发过：它必须原样留在 <c>PENDING_LOAD</c>。</summary>
    private const string OnTheWayDemandId = "D-ON-THE-WAY";

    /// <summary>已经装完的那一条：它已经是 <c>LOADED</c>，迁移不该碰。</summary>
    private const string LoadedDemandId = "D-LOADED";

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheDemandBeingLoadedWhenTheServerIsUpgradedIsBackFilledAndNothingElseMoves()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Context.GetService<IMigrator>().MigrateAsync(PreviousMigration, cancellationToken);
        await SeedJourneysAsOldVersionLeftThemAsync(fixture);

        await fixture.Context.Database.MigrateAsync(cancellationToken);

        Assert.Equal(JourneyDemandStatuses.Loading, await StatusAsync(fixture, LoadingDemandId));
        Assert.Equal(JourneyDemandStatuses.PendingLoad, await StatusAsync(fixture, OnTheWayDemandId));
        Assert.Equal(JourneyDemandStatuses.Loaded, await StatusAsync(fixture, LoadedDemandId));
    }

    /// <summary>
    /// 回滚回本提交之前：<c>LOADING</c> 退回 <c>PENDING_LOAD</c>，因为那一版的代码不认识它。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task RollingBackReturnsTheLoadingMembershipToPendingLoad()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Context.GetService<IMigrator>().MigrateAsync(PreviousMigration, cancellationToken);
        await SeedJourneysAsOldVersionLeftThemAsync(fixture);
        await fixture.Context.Database.MigrateAsync(cancellationToken);
        // 先确认 Up 真的写下了 LOADING：没有这一句，Up 失效时这条用例照样绿——它断言的「回滚后是 PENDING_LOAD」
        // 在「从来没被改过」时也成立。前置断言让它测的是回滚，而不是「反正是 PENDING_LOAD」。
        Assert.Equal(JourneyDemandStatuses.Loading, await StatusAsync(fixture, LoadingDemandId));

        await fixture.Context.GetService<IMigrator>().MigrateAsync(PreviousMigration, cancellationToken);

        Assert.Equal(JourneyDemandStatuses.PendingLoad, await StatusAsync(fixture, LoadingDemandId));
        Assert.Equal(JourneyDemandStatuses.Loaded, await StatusAsync(fixture, LoadedDemandId));
    }

    /// <summary>
    /// 三趟旧版留下的旅程：一趟正在装、一趟还在路上、一趟已经装完。三条归属此刻都带着旧版写下的状态。
    /// </summary>
    /// <remarks>
    /// 旧版从不写 <c>LOADING</c>，所以「正在装」那一趟的归属也是 <c>PENDING_LOAD</c>——这正是迁移要认出来的那一条，
    /// 而认出它靠的是旅程的阶段，不是归属本身。
    /// </remarks>
    private static async Task SeedJourneysAsOldVersionLeftThemAsync(Batch7JourneyFixture fixture)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateTimeOffset now = Batch7JourneyFixture.Now;
        ControlServerDbContext context = fixture.Context;
        await Batch7JourneyFixture.AcceptAsync(context, LoadingDemandId, "agv-01", "VK-01", now);
        await Batch7JourneyFixture.AcceptAsync(context, OnTheWayDemandId, "agv-02", "VK-02", now);
        await Batch7JourneyFixture.AcceptAsync(context, LoadedDemandId, "agv-03", "VK-03", now);

        await SetStageAsync(context, LoadingDemandId, JourneyRuntimeStage.AwaitingLoadResult);
        await SetStageAsync(context, OnTheWayDemandId, JourneyRuntimeStage.AwaitingPickupArrival);
        // 已经装完的那一趟也停在 AwaitingLoadResult 之后的某个阶段，而它的归属已经是 LOADED：
        // 迁移的谓词必须同时看阶段和归属状态，只看阶段会把它一起刷掉。
        await SetStageAsync(context, LoadedDemandId, JourneyRuntimeStage.AwaitingGateArrival);
        (await context.Set<JourneyDemandRow>()
                .SingleAsync(row => row.DemandId == LoadedDemandId, cancellationToken))
            .Status = JourneyDemandStatuses.Loaded;
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();
    }

    private static async Task SetStageAsync(
        ControlServerDbContext context, string demandId, JourneyRuntimeStage stage) =>
        (await context.JourneyRuntimes.SingleAsync(
            row => row.DemandId == demandId, TestContext.Current.CancellationToken)).Stage = stage;

    private static async Task<string> StatusAsync(Batch7JourneyFixture fixture, string demandId)
    {
        ControlServerDbContext context = fixture.NewContext();
        return (await context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == demandId, TestContext.Current.CancellationToken)).Status;
    }
}
