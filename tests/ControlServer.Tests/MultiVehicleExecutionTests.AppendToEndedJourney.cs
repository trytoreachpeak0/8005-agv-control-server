using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ControlServer.Tests;

/// <summary>
/// 派车轮选中「往这趟旅程追加」之后、提交之前旅程结束了（control-server#864）：这一轮放弃这次追加，需求留在积压里下一轮重新分配，
/// 记一条带原因码的信息日志，不抛异常、不记 2123／2124。
/// </summary>
/// <remarks>
/// <para>
/// 生产上三天三次（包 <c>9c8c34a5</c>）：10-21 22:51 与 10-23 10:55 是 <c>DispatchRoundRunner.AppendToJourneyAsync</c> 读旅程时
/// 「Sequence contains no elements」，10-23 10:50 是 <c>WireToGateStore.StageAndCommitAppendAsync</c> 在事务里判出
/// 「Journey … is Completed and cannot take an appended demand」，记 Error 2124。两处是同一个竞态的两个时刻：
/// 旅程结束落在派车轮读旅程<b>之前</b>，派车轮那一读读空；落在读旅程<b>之后</b>、写库事务之前，事务里那一判撞上。
/// </para>
/// <para>
/// 两个时刻都造在 RIoT 读车上，与 <see cref="AJourneyThatEndsBetweenTheAppendDecisionAndTheAppendIsSkippedForTheRound"/> 同一个办法：
/// 这一轮对这辆车的第二次读车在 <c>FinalDynamicFactsReadyAsync</c> 里（读旅程之前），第三次在最终准入门里——
/// <c>DemandIntakeService</c> 重读目录之后、调 <c>WireToGateStore.AppendToJourneyAsync</c> 之前，也就是读旅程之后、写库事务之前。
/// </para>
/// <para>
/// 结束的形状照 <c>JourneyClosure.StageAsync</c> 与 <c>JourneyPurposeClaimRelease</c>：阶段 Completed、收尾码（取消为
/// <c>CANCELLED_BY_OPERATOR</c>，正常卸完为 null）、需求归属终结、用途占有释放。写库这一侧只判阶段，其余几样是为了让
/// 「下一轮能派出去」那一半测的是真实结束之后的车，而不是一辆仍被占着的车。
/// </para>
/// </remarks>
public sealed partial class MultiVehicleExecutionTests
{
    /// <summary>派车轮读旅程之前（第二次读车）。修前 <c>First()</c> 在这里抛；cs#791 改成静默拒收、记「最终动态事实不就绪」。</summary>
    private const string BeforeTheRunnerReadsTheJourney = "before-runner-read";

    /// <summary>读旅程之后、写库事务之前（第三次读车，最终准入门里）。修前事务里抛 BusinessIdentityConflictException，记 2124。</summary>
    private const string BeforeTheStoreTransaction = "before-store-transaction";

    /// <summary>
    /// 两个时刻 × 两种结束：都不抛、不记 2123／2124，需求不进这趟旅程、积压行记 <see cref="DispatchReasonCodes.FinalAppendJourneyEnded"/>、
    /// 未受理，记一条 2146 信息日志；这辆车跑完了这一轮。下一轮这条需求由这辆（已经空下来的）车建新旅程接走。
    /// </summary>
    [Theory]
    [InlineData(BeforeTheRunnerReadsTheJourney, "cancelled")]
    [InlineData(BeforeTheRunnerReadsTheJourney, "delivered")]
    [InlineData(BeforeTheStoreTransaction, "cancelled")]
    [InlineData(BeforeTheStoreTransaction, "delivered")]
    public async Task AnAppendToAJourneyThatEndedBeforeItsCommitIsDroppedForTheRoundAndTheDemandWaitsForTheNext(
        string moment, string ending)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..1], withRouteGraph: true);
        await fixture.AllowEnRouteAppendAsync(1_000_000);
        AcceptedDemandSnapshot firstDemand = FleetFixture.Demand(0, "N1-1", 0);
        AcceptedDemandSnapshot second = FleetFixture.Demand(1, "N1-2", 1);
        fixture.Catalog.Set([firstDemand]);
        await fixture.RunRoundAsync();
        JourneyRuntimeRow first = Assert.Single(await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(token));
        fixture.EngineLog.Entries.Clear();
        fixture.RoundOutcomes.Outcomes.Clear();

        EndedFlag ended = EndTheJourneyOnRead(
            fixture, FleetFixture.VehicleKeys[0], moment == BeforeTheRunnerReadsTheJourney ? 2 : 3, first.JourneyId, ending);
        fixture.Catalog.Set([firstDemand, second]);
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        Assert.True(ended.Ended, "the round never reached the read the journey was to end on");
        AssertNoRoundFault(fixture);
        Assert.Equal(
            [FleetFixture.AgvIds[0]],
            Assert.Single(fixture.RoundOutcomes.Outcomes).CompletedVehicles.Select(vehicle => vehicle.AgvId).ToArray());
        // 这趟旅程上仍只有第一条，第二条没被写进任何东西。
        JourneyDemandRow membership = Assert.Single(await fixture.Context.Set<JourneyDemandRow>().AsNoTracking().ToArrayAsync(token));
        Assert.Equal(firstDemand.DemandId, membership.DemandId);
        Assert.False(await fixture.Context.AcceptedDemands.AsNoTracking().AnyAsync(row => row.DemandId == second.DemandId, token));
        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.AsNoTracking()
            .SingleAsync(row => row.DemandId == second.DemandId, token);
        Assert.Null(backlog.AcceptedAt);
        Assert.Equal(DispatchReasonCodes.FinalAppendJourneyEnded, backlog.ReasonCode);
        EventRecordingLogger<Host.Runtime.JourneyRuntimeEngine>.Entry dropped = Assert.Single(
            fixture.EngineLog.Entries, entry => entry.EventId.Id == 2146);
        Assert.Equal(LogLevel.Information, dropped.Level);
        Assert.Contains(first.JourneyId, dropped.Message, StringComparison.Ordinal);
        Assert.Contains(second.DemandId, dropped.Message, StringComparison.Ordinal);
        // 日志自己说出这单的去向：留在积压、下一轮重新分配（Coordinator12 10-25：生产日志看不出被跳过的需求后来补派没有）。
        Assert.Contains("stays in the backlog", dropped.Message, StringComparison.Ordinal);
        Assert.Contains("reassigned next round", dropped.Message, StringComparison.Ordinal);
        Assert.Contains(DispatchReasonCodes.FinalAppendJourneyEnded, dropped.Message, StringComparison.Ordinal);
        Assert.Null(dropped.Error);
        // 这一轮还没有人接走它，不该有「补派」那一条。
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry => entry.EventId.Id == 2147);

        // 下一轮：车已经空下来，这条需求建一趟新旅程被接走。
        fixture.Riot.OnVehicleRead = null;
        fixture.EngineLog.Entries.Clear();
        fixture.Catalog.Set([second]);
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        AssertNoRoundFault(fixture);
        Assert.NotNull((await fixture.Context.JourneyBacklog.AsNoTracking()
            .SingleAsync(row => row.DemandId == second.DemandId, token)).AcceptedAt);
        JourneyDemandRow taken = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == second.DemandId, token);
        Assert.NotEqual(first.JourneyId, taken.JourneyId);
        // 补派留痕：同一个需求号、接走它的车、建的是新旅程，与上一轮的 2146 按需求号对得上。
        EventRecordingLogger<Host.Runtime.JourneyRuntimeEngine>.Entry reassigned = Assert.Single(
            fixture.EngineLog.Entries, entry => entry.EventId.Id == 2147);
        Assert.Equal(LogLevel.Information, reassigned.Level);
        Assert.Contains(second.DemandId, reassigned.Message, StringComparison.Ordinal);
        Assert.Contains(FleetFixture.AgvIds[0], reassigned.Message, StringComparison.Ordinal);
        Assert.Contains(taken.JourneyId, reassigned.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry => entry.EventId.Id == 2146);
    }

    /// <summary>
    /// 对照：读旅程之后、写库之前旅程<b>没</b>结束（第三次读车照常），追加照常进这一趟旅程，没有 2146。
    /// 上一条的判别力在这里：两者只差那一次读车上有没有把旅程收尾。
    /// </summary>
    [Fact]
    public async Task AnAppendToAJourneyStillRunningAtItsCommitIsTaken()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..1], withRouteGraph: true);
        await fixture.AllowEnRouteAppendAsync(1_000_000);
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        await fixture.RunRoundAsync();
        JourneyRuntimeRow first = Assert.Single(await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(token));
        fixture.EngineLog.Entries.Clear();

        int reads = 0;
        fixture.Riot.OnVehicleRead = vehicleKey =>
        {
            if (vehicleKey == FleetFixture.VehicleKeys[0])
            {
                reads++;
            }
        };
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0), FleetFixture.Demand(1, "N1-2", 1)]);
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        // 第三次读车确实发生了：上一条在这一次读上收尾，不是落在一次不存在的读上。
        Assert.True(reads >= 3, $"the round read the vehicle {reads} time(s); the store-side moment needs a third");
        AssertNoRoundFault(fixture);
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry => entry.EventId.Id == 2146);
        // 补派那一条只给上一轮因旅程结束被放弃的需求，平常的受理不记。
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry => entry.EventId.Id == 2147);
        JourneyDemandRow[] memberships = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking().ToArrayAsync(token);
        Assert.Equal([first.JourneyId, first.JourneyId], memberships.Select(row => row.JourneyId));
    }

    /// <summary>
    /// 同一轮里别的车不受影响：在途车甲的追加撞上旅程结束（写库事务那一侧），空闲车乙照常接走它自己那条需求。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两辆车、两个分区（照 <see cref="EachCandidateOfOneRoundIsRoutedIntoItsAssignedZoneAndJudgedAgainstThatZonesVehicles"/>）：
    /// 甲只接 N1-1、N1-2 所在的区，乙只接 N1-3 所在的区。N1-2 那条只有甲能接，所以它一定走追加；N1-3 那条只有乙能接。
    /// 两条在同一轮里，N1-2 那条排在前面（候选按创建时刻），甲的追加先发生、乙在它之后。
    /// </para>
    /// <para>
    /// 修前甲那一段抛异常，被每车隔离接住：甲退出本轮、记 2124，乙照常接走它那条。所以乙那一半修前修后都成立，先判它；
    /// 修前的红落在后面的 2124 上。它守的是修后不比修前更糟（例如把旅程结束当成整轮的错误往外抛，乙就接不到了）。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnotherVehicleInTheSameRoundTakesItsOwnDemandWhileTheAppendIsDropped()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        const string ZoneOfTheAppend = "MAP-25-ZONE-A";
        const string ZoneOfTheOther = "MAP-25-ZONE-B";
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options =>
            {
                options.Fleet = options.Fleet[..2];
                options.DispatchZone = ZoneOfTheAppend;
                options.AllowedDispatchZones = [ZoneOfTheAppend, ZoneOfTheOther];
                options.Fleet[0].Zones = [ZoneOfTheAppend];
                options.Fleet[1].Zones = [ZoneOfTheOther];
            },
            withRouteGraph: true);
        await fixture.AreaAssignments.ImportAsync(
            [
                new("N1-1", ZoneOfTheAppend, "FRONT"),
                new("N1-2", ZoneOfTheAppend, "FRONT"),
                new("N1-3", ZoneOfTheOther, "FRONT"),
            ],
            Now);
        // 夹具路网里没有 N1-3 的取货站（14），乙会被判取货不可达；照 N1-2_N1-3 那一站的位置补上。
        await fixture.ReplaceRouteGraphAsync(
            [
                new(1, 1, 2, 10000, 0, 0, 10000, 0, 1, false),
                new(2, 2, 3, 10000, 10000, 0, 20000, 0, 1, false),
                new(3, 3, 4, 10000, 20000, 0, 30000, 0, 1, false),
                new(4, 4, 5, 10000, 30000, 0, 40000, 0, 1, false),
            ],
            [
                new(300, "等待点", 1, 0, 0, 1, 0),
                new(12, "N1-1", 1, 10000, 0, 2, 0),
                new(13, "N1-2_N1-3", 2, 20000, 0, 3, 0),
                new(14, "N1-3", 2, 20000, 0, 3, 0),
                new(210, "关卡", 3, 30000, 0, 4, 0),
            ]);
        await fixture.AllowEnRouteAppendAsync(1_000_000);
        AcceptedDemandSnapshot firstDemand = FleetFixture.Demand(0, "N1-1", 0);
        fixture.Catalog.Set([firstDemand]);
        await fixture.RunRoundAsync();
        JourneyRuntimeRow first = await fixture.JourneyOfAsync(FleetFixture.AgvIds[0]);
        fixture.EngineLog.Entries.Clear();
        fixture.RoundOutcomes.Outcomes.Clear();

        AcceptedDemandSnapshot appended = FleetFixture.Demand(1, "N1-2", 1);
        AcceptedDemandSnapshot other = FleetFixture.Demand(2, "N1-3", 2);
        EndedFlag ended = EndTheJourneyOnRead(fixture, FleetFixture.VehicleKeys[0], 3, first.JourneyId, "cancelled");
        fixture.Catalog.Set([firstDemand, appended, other]);
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        Assert.True(ended.Ended, "the round never reached the read the journey was to end on");
        // 乙先判：修前它也成立，红只落在下面甲那一半上。
        JourneyRuntimeRow otherJourney = await fixture.JourneyOfAsync(FleetFixture.AgvIds[1]);
        Assert.Equal(other.DemandId, otherJourney.DemandId);
        Assert.NotNull((await fixture.Context.JourneyBacklog.AsNoTracking()
            .SingleAsync(row => row.DemandId == other.DemandId, token)).AcceptedAt);
        AssertNoRoundFault(fixture);
        Assert.Equal(
            DispatchReasonCodes.FinalAppendJourneyEnded,
            (await fixture.Context.JourneyBacklog.AsNoTracking().SingleAsync(row => row.DemandId == appended.DemandId, token))
            .ReasonCode);
        // 甲也跑完了这一轮（修前它被隔离、退出本轮，不在这里）。
        Assert.Equal(
            [FleetFixture.AgvIds[0], FleetFixture.AgvIds[1]],
            Assert.Single(fixture.RoundOutcomes.Outcomes).CompletedVehicles.Select(vehicle => vehicle.AgvId)
                .Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// 会话因本服务端自己的在途单而未就绪（真车载端行驶全程都是这个状态）：在途车在资格判断上就被拒，走不到追加，
    /// 也就走不到这张票的两处——不抛、不记 2146，积压行仍是读不到车载端事实，这条需求留到下一轮。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 新逻辑只读旅程阶段，不读会话。这一条钉住两件事：会话未就绪时新逻辑不被走到、也不盖掉那一条既有理由；以及「走不到」
    /// 的机理——这一轮只读这辆车一次（入轮那一次），而追加前的两次读车（<c>FinalDynamicFactsReadyAsync</c> 与最终准入门）都没发生。
    /// 所以真车在路上时，本票的竞态只能发生在车停站、会话回到就绪的那段时间里。
    /// </para>
    /// <para>
    /// 安全摘要与 RIoT 行驶读数照 <see cref="AnAppendWhileTheVehicleIsDrivingIsRefusedBecauseTheOnboardIsNotReady"/>。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AVehicleWhoseSessionIsNotReadyOnItsOwnOrderNeverReachesTheAppend()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using FleetFixture fixture = await DriveFirstDemandAsync();
        JourneyRuntimeRow first = await fixture.JourneyOfAsync(FleetFixture.AgvIds[0]);
        ReportRiotDriving(fixture, first.VehicleKey, moving: true);
        SessionReadinessDecision decision = await ReportOnboardSafetyAsync(fixture, FleetFixture.AgvIds[0], MovingSummary);
        Assert.Equal(
            (SessionReadiness.RecoveryRequired, "DEPARTURE_SAFETY_NOT_READY"),
            (decision.Readiness, decision.ReasonCode));
        fixture.EngineLog.Entries.Clear();

        // 追加前那次读车上收尾；它不发生，旅程就不会被收尾。
        EndedFlag ended = EndTheJourneyOnRead(fixture, first.VehicleKey, 2, first.JourneyId, "cancelled");
        AcceptedDemandSnapshot second = FleetFixture.Demand(1, "N1-2", 1);
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0), second]);
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, ended.Reads);
        Assert.False(ended.Ended);
        AssertNoRoundFault(fixture);
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry => entry.EventId.Id == 2146);
        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.AsNoTracking()
            .SingleAsync(row => row.DemandId == second.DemandId, token);
        Assert.Null(backlog.AcceptedAt);
        Assert.Equal("ONBOARD_FACTS_NOT_READY", backlog.ReasonCode);
        Assert.Single(await fixture.Context.Set<JourneyDemandRow>().AsNoTracking().ToArrayAsync(token));
    }

    /// <summary>
    /// 一条挂着结构性阻断的需求，本轮追加撞上旅程结束：阻断熬过这一轮——它没有被接走，什么也没证明阻断不再成立。
    /// </summary>
    /// <remarks>
    /// <para>
    /// cs#242 那三条（<see cref="ACandidateChangedAtIntakeDoesNotClearAStructuralBlock"/> 等）的第四条：认领在调受理之前就记进
    /// 本轮已接走的集合，轮末的结构性告警按那个集合清阻断，所以受理拒收的需求要记进 <c>ClaimsIntakeRefused</c> 扣掉。
    /// <c>JourneyEnded</c> 漏记时，轮末误清阻断（2115）、下一轮又立起（2114），告警闪一下；防饥饿升级读同一个集合，晚一轮。
    /// </para>
    /// <para>
    /// 独立审查 S1（control-server#864）：删掉 <c>or DemandIntakeOutcome.JourneyEnded</c> 时本 PR 原有用例全绿。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(BeforeTheRunnerReadsTheJourney)]
    [InlineData(BeforeTheStoreTransaction)]
    public async Task AnAppendWhoseJourneyEndedDoesNotClearAStructuralBlock(string moment)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..1], withRouteGraph: true);
        await fixture.AllowEnRouteAppendAsync(1_000_000);
        AcceptedDemandSnapshot firstDemand = FleetFixture.Demand(0, "N1-1", 0);
        AcceptedDemandSnapshot second = FleetFixture.Demand(1, "N1-2", 1);
        fixture.Catalog.Set([firstDemand]);
        await fixture.RunRoundAsync();
        JourneyRuntimeRow first = Assert.Single(await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(token));

        DateTimeOffset raisedAt = Now.AddMinutes(-30);
        EventRecordingLogger<StructuralDispatchBlockSink> blockLog = await RaiseStandingBlockAsync(fixture, second, raisedAt);
        EndedFlag ended = EndTheJourneyOnRead(
            fixture, FleetFixture.VehicleKeys[0], moment == BeforeTheRunnerReadsTheJourney ? 2 : 3, first.JourneyId, "cancelled");
        fixture.Catalog.Set([firstDemand, second]);
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        Assert.True(ended.Ended, "the round never reached the read the journey was to end on");
        Assert.Equal(
            DispatchReasonCodes.FinalAppendJourneyEnded,
            (await fixture.Context.JourneyBacklog.AsNoTracking().SingleAsync(row => row.DemandId == second.DemandId, token))
            .ReasonCode);
        await AssertBlockSurvivedTheRoundAsync(fixture, blockLog, raisedAt);
    }

    /// <summary>
    /// 这一轮对 <paramref name="vehicleKey"/> 的第 <paramref name="onRead"/> 次读车时，照真实收尾的形状结束这趟旅程。
    /// 返回的标志在轮后读：是否真的结束了。
    /// </summary>
    private static EndedFlag EndTheJourneyOnRead(
        FleetFixture fixture, string vehicleKey, int onRead, string journeyId, string ending)
    {
        EndedFlag flag = new();
        fixture.Riot.OnVehicleRead = key =>
        {
            if (key != vehicleKey || ++flag.Reads != onRead)
            {
                return;
            }

            string? closure = ending == "cancelled" ? "CANCELLED_BY_OPERATOR" : null;
            string demandStatus = ending == "cancelled" ? JourneyDemandStatuses.Terminated : JourneyDemandStatuses.Unloaded;
            using ControlServerDbContext other = fixture.NewContext();
            other.JourneyRuntimes
                .Where(row => row.JourneyId == journeyId)
                .ExecuteUpdate(set => set
                    .SetProperty(row => row.Stage, JourneyRuntimeStage.Completed)
                    .SetProperty(row => row.BlockReasonCode, closure));
            other.Set<JourneyDemandRow>()
                .Where(row => row.JourneyId == journeyId)
                .ExecuteUpdate(set => set.SetProperty(row => row.Status, demandStatus));
            other.Set<VehiclePurposeClaimRow>()
                .Where(row => row.JourneyId == journeyId)
                .ExecuteDelete();
            flag.Ended = true;
        };
        return flag;
    }

    /// <summary>轮后读的标志；<see cref="EndTheJourneyOnRead"/> 在轮前就返回，所以要一个引用。</summary>
    private sealed class EndedFlag
    {
        public bool Ended { get; set; }

        /// <summary>这一轮读了这辆车几次。</summary>
        public int Reads { get; set; }
    }


    private static void AssertNoRoundFault(FleetFixture fixture) =>
        Assert.DoesNotContain(
            fixture.EngineLog.Entries,
            entry => entry.EventId.Id is 2123 or 2124 || entry.Level >= LogLevel.Error);
}
