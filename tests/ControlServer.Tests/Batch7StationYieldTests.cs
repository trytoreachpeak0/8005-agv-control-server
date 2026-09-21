using System.Data.Common;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.TaskTypeStations;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using static ControlServer.Tests.Batch7CargoHoldingTests;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 让站在推进段上的样子（批次7-08，control-server#213；REQ-0355，规格第 5.1 节第 8 条，出口判据 ③）。
/// </summary>
/// <remarks>
/// <para>
/// <b>「别的车」是确定性造出来的，不靠时序。</b>夹具只有一辆会跑的车（它的会话、它的 RIoT）。另一辆车的旅程走真实受理路径
/// （<see cref="WireToGateStore"/>）种进同一个库：让站只看那趟旅程的停靠与阶段，不看那辆车跑没跑。那辆车没有会话，推进段每轮
/// 在就绪门前为它记一个 <c>ONBOARD_SESSION_NOT_READY</c> 就返回，不碰它的阶段与装货阶段。
/// </para>
/// <para>
/// <b>判据断到车上看得见的那一层</b>：让站之后车收到的 <c>loadingPhase</c>，以及服务端有没有为离站向 RIoT 要移动。
/// 列是服务端自己的记录，两者可以不一致，那正是要防的。
/// </para>
/// </remarks>
public sealed class Batch7StationYieldTests
{
    private const string ComerAgv = "AGV-COMER";
    private const string ComerKey = "KEY-COMER";
    private const string ComerDemand = "40000000-0000-4000-8000-000000000001";
    private const string HolderAgv = "AGV-HOLDER";
    private const string HolderKey = "KEY-HOLDER";
    private const string HolderDemand = "40000000-0000-4000-8000-000000000002";

    /// <summary>
    /// 判据 ③ 前半与 ⑥ 的让站那一半：车在站上持货等单，本轮判定继续等；随后另一辆车受理、下一停靠就是这个站。
    /// 下一轮让站——快照是 <c>CLOSED/WAITING_STATION_YIELD</c>，车为离站发核验、拿到答复后去卸货，卸货到站那一张也还是这个原因。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这就是票面「并发读改写」那个交错</b>：一轮之内先推进在途车、后跑派车轮（<c>ExecuteOnceAsync</c>），所以「A 本轮已判定
    /// 继续等单、随后 B 受理」是这台服务器的常态次序。受理用的是推进段自己的上下文——派车轮与推进段共用它——所以等单车那一行
    /// 在这里是同一个被跟踪的实例，被受理事务改过之后下一轮读到的是改过的值。
    /// </para>
    /// <para>
    /// 触发时刻取受理的那一刻：不早于受理，这是 L2 场景拿去与受理时刻比的那个量。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AVehicleCommittedToTheStationMakesTheHolderYieldAndLeave()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        DateTimeOffset closure = await LoadTheFirstDemandAsync(fixture);
        await TickAndRunAsync(fixture);
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, (await JourneyOfAsync(fixture, FirstDemandId)).LoadingPhaseState);

        DateTimeOffset acceptedAt = fixture.Clock.GetUtcNow();
        await AcceptComerAsync(fixture.Context, await HolderStationAsync(fixture), acceptedAt);
        Assert.False(await DepartureCheckSentAsync(fixture), "The holder left before its next round judged the yield.");
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow holder = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(LoadingPhaseStates.Closed, holder.LoadingPhaseState);
        Assert.Equal(LoadingClosedReasons.WaitingStationYield, holder.LoadingClosedReason);
        Assert.Equal(acceptedAt, holder.YieldTriggeredAt);
        Assert.Equal(ComerKey, holder.YieldTriggeredByVehicleKey);
        Assert.True(await DepartureCheckSentAsync(fixture), "The holder yielded and was never asked to leave.");

        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));
        await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_GATE");

        LoadingPhaseSnapshot[] snapshots = await LoadingPhaseSnapshotsAsync(fixture);
        Assert.Equal(
            [
                (LoadingPhaseStates.Loading, (string?)null),
                (LoadingPhaseStates.CargoHoldingWait, null),
                (LoadingPhaseStates.Closed, LoadingClosedReasons.WaitingStationYield),
                (LoadingPhaseStates.Closed, LoadingClosedReasons.WaitingStationYield),
            ],
            snapshots.Select(snapshot => (snapshot.State, snapshot.ClosedReason)));
        // 让站不清期限（program#94：进入 CLOSED 后保留原值）。
        Assert.All(snapshots[1..], snapshot => Assert.Equal(closure + TimeSpan.FromMinutes(30), snapshot.CargoHoldingDeadlineAt));
        AssertStrictlyIncreasing(snapshots);
    }

    /// <summary>
    /// 让站之后不再接单（REQ-0354 末句）：本可追加进来的需求，在追加事务里就被拒，库里这趟旅程仍只有原来那一条。
    /// </summary>
    /// <remarks>
    /// 追加走 <c>Batch7ThreeStopJourneyTests.AppendDemandAsync</c>——它绕过派车轮，直接进追加事务，所以挡住它的只能是事务里
    /// 那道「装货阶段已关闭」。派车轮那一道（<c>LoadingPhaseOpenCriterion</c>）读的是同一列，由 L2 场景 <c>waiting-station-yield</c>
    /// 在真派车里取证。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AfterYieldingTheVehicleTakesNoFurtherDemand()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        await LoadTheFirstDemandAsync(fixture);
        await AcceptComerAsync(fixture.Context, await HolderStationAsync(fixture), fixture.Clock.GetUtcNow());
        await TickAndRunAsync(fixture);
        Assert.Equal(LoadingClosedReasons.WaitingStationYield, (await JourneyOfAsync(fixture, FirstDemandId)).LoadingClosedReason);

        fixture.BoxCounts.Set(SecondSublot, 7);
        BusinessIdentityConflictException refused = await Assert.ThrowsAsync<BusinessIdentityConflictException>(
            () => Batch7ThreeStopJourneyTests.AppendDemandAsync(fixture, SecondDemandId, SecondSublot, "N1-2", 13));
        Assert.Contains(LoadingClosedReasons.WaitingStationYield, refused.Message, StringComparison.Ordinal);
        Assert.Equal(1, await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .CountAsync(row => row.JourneyId == JourneyIdentity.ForAnchorDemand(FirstDemandId), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 触发已经落库、被让的车还没轮到下一轮判定：这时的追加照样被拒（审查必修 1，REQ-0354 末句）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 同一个派车轮里先受理乙、后追加戊是常态：受理事务给等单车写上触发，而它的装货阶段列要到它自己下一轮才变 CLOSED。
    /// 追加事务原先只看那一列，戊就在这个窗口里进了车。上一条用例在两步之间多跑了一轮，所以看不见这个窗口——这一条两步紧挨着，
    /// 中间不跑引擎。
    /// </para>
    /// <para>
    /// 挡它的是追加事务里的那一道（<c>WireToGateStore.StageAndCommitAppendAsync</c> 读 <c>YieldTriggeredAt</c>），与受理写触发
    /// 在同一个库上串行，所以没有第二个窗口。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnAppendRightAfterTheTriggerIsRefusedBeforeTheHolderRunsAgain()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        await LoadTheFirstDemandAsync(fixture);
        await AcceptComerAsync(fixture.Context, await HolderStationAsync(fixture), fixture.Clock.GetUtcNow());
        JourneyRuntimeRow triggered = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.NotNull(triggered.YieldTriggeredAt);
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, triggered.LoadingPhaseState);

        fixture.BoxCounts.Set(SecondSublot, 7);
        await Assert.ThrowsAsync<BusinessIdentityConflictException>(
            () => Batch7ThreeStopJourneyTests.AppendDemandAsync(fixture, SecondDemandId, SecondSublot, "N1-2", 13));
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(1, await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .CountAsync(row => row.JourneyId == JourneyIdentity.ForAnchorDemand(FirstDemandId), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 满了、本站已承诺的那一条还没录入时被让站：快照当场是 <c>WAITING_STATION_YIELD</c>，但录入、装货照常做完，离站核验要到
    /// 装货落定之后才发（票面第 2、3 条：本站已承诺的照常处理，包括正在录入、正在执行的；不打断阻断离站的状态）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AFullVehicleYieldsAtOnceButFinishesTheLoadItIsCommittedToBeforeLeaving()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await TickAndRunAsync(fixture);
        await LeaveOnlyTheFirstDemandsSlotsAsync(fixture);
        await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_PICKUP");
        Assert.Equal(LoadingPhaseStates.VehicleFull, (await JourneyOfAsync(fixture, FirstDemandId)).LoadingPhaseState);

        await AcceptComerAsync(fixture.Context, await HolderStationAsync(fixture), fixture.Clock.GetUtcNow());
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow yielded = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, yielded.Stage);
        Assert.Equal(LoadingClosedReasons.WaitingStationYield, yielded.LoadingClosedReason);
        Assert.Equal(
            (LoadingPhaseStates.Closed, LoadingClosedReasons.WaitingStationYield),
            (await LoadingPhaseSnapshotsAsync(fixture)) is [.., var last] ? (last.State, last.ClosedReason) : default);

        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        Assert.False(await DepartureCheckSentAsync(fixture), "The yielding vehicle was asked to leave mid-load.");

        await SettleLoadAsync(fixture, FirstDemandId);
        Assert.True(await DepartureCheckSentAsync(fixture), "The load settled and the yielding vehicle never left.");
        Assert.Equal(
            JourneyDemandStatuses.Loaded,
            (await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
                .SingleAsync(row => row.DemandId == FirstDemandId, TestContext.Current.CancellationToken)).Status);
    }

    /// <summary>
    /// 仓门没锁（开门）、状态未知：让站已触发，快照已是 <c>WAITING_STATION_YIELD</c>，离站核验照常问，但车载端答「不能走」，
    /// 服务端就不为离站向 RIoT 要移动——复用既有离站路，不另写一套「阻断离站」。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [InlineData("door open", false, false)]
    [InlineData("state unknown", true, true)]
    public async Task AYieldingVehicleWhoseDepartureIsUnsafeDoesNotMove(string why, bool slotsLocked, bool unknownPresent)
    {
        _ = why;
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        await LoadTheFirstDemandAsync(fixture);
        await AcceptComerAsync(fixture.Context, await HolderStationAsync(fixture), fixture.Clock.GetUtcNow());
        await TickAndRunAsync(fixture);
        Assert.True(await DepartureCheckSentAsync(fixture));

        JourneyStopRow stop = await CurrentStopAsync(fixture, FirstDemandId);
        await AddInboxAsync(
            fixture,
            FirstSafetyResultId,
            "PreDepartureSafetyCheckResult",
            UnsafeDepartureAnswer(fixture, stop.DepartureSafetyCheckId!, slotsLocked, unknownPresent),
            stop.DepartureSafetyCheckMessageId);
        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow holder = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, holder.Stage);
        Assert.Equal(LoadingClosedReasons.WaitingStationYield, holder.LoadingClosedReason);
        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal(
            LoadingClosedReasons.WaitingStationYield,
            (await LoadingPhaseSnapshotsAsync(fixture))[^1].ClosedReason);
    }

    /// <summary>
    /// 装后纠错进行中：让站已触发，离站等待期限也过了，纠错没结就不发离站核验（<c>LOAD_CORRECTION_IN_PROGRESS</c>）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AYieldingVehicleWithACorrectionOpenIsNotAskedToLeave()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        await LoadTheFirstDemandAsync(fixture);
        JourneyDemandRow membership = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == FirstDemandId, TestContext.Current.CancellationToken);
        StationOperationRow load = await fixture.Context.StationOperations.AsNoTracking()
            .SingleAsync(row => row.SlotOperationAttemptId == membership.LoadSlotOperationAttemptId, TestContext.Current.CancellationToken);
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            await fixture.RequestLoadCorrectionOnConnectionAsync(
                connection,
                "70000000-0000-4000-8000-000000000031",
                FirstDemandId,
                load.SlotOperationAttemptId,
                System.Text.Json.JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!);
        }
        Assert.Contains("LoadCorrectionCommand", await fixture.OutboxTypesAsync());

        await AcceptComerAsync(fixture.Context, await HolderStationAsync(fixture), fixture.Clock.GetUtcNow());
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow holder = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(LoadingClosedReasons.WaitingStationYield, holder.LoadingClosedReason);
        Assert.Equal("LOAD_CORRECTION_IN_PROGRESS", holder.BlockReasonCode);
        Assert.False(await DepartureCheckSentAsync(fixture), "A vehicle with a correction open was asked to leave.");
    }

    /// <summary>
    /// 纠错开在<b>追加进来的</b>那条需求上，也挡住离站（本票实读发现、一并修的缺陷）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 纠错的授权按归属找旅程，追加的需求能开纠错；离站判定原先只按旅程行上的锚需求查纠错，于是这一条开着，站点等待一到车照样
    /// 发离站核验。让站之后车要走，这正是票面第 3 条「进行中的纠错不被打断」会被穿过去的那条路，所以这里不带让站、单独钉住它：
    /// 带上让站，绿的理由就混进了「装货阶段关了」。
    /// </para>
    /// <para>
    /// 三停靠旅程：第一条在 12 号站，第二条追加、在 13 号站。车在第二站装完、站点等待期间操作员对第二条发起纠错。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnOpenCorrectionOnAnAppendedDemandHoldsTheDeparture()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(
            fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)),
            fixture.Demand(SecondDemandId, SecondSublot, Now.AddMinutes(-9), area: "N1-2"));
        fixture.BoxCounts.Set(FirstSublot, 7);
        fixture.BoxCounts.Set(SecondSublot, 7);
        await TickAndRunAsync(fixture);
        await Batch7ThreeStopJourneyTests.AppendDemandAsync(fixture, SecondDemandId, SecondSublot, "N1-2", 13);

        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        fixture.Clock.Advance(TimeSpan.FromSeconds(15));
        await TickAndRunAsync(fixture);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        await ArriveAtCurrentStopAsync(fixture, SecondDemandId, "TO_GATE");
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        await SettleLoadAsync(fixture, SecondDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await JourneyOfAsync(fixture, SecondDemandId)).Stage);
        int checksBeforeTheCorrection = await DepartureChecksSentAsync(fixture);

        JourneyDemandRow second = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == SecondDemandId, token);
        StationOperationRow load = await fixture.Context.StationOperations.AsNoTracking()
            .SingleAsync(row => row.SlotOperationAttemptId == second.LoadSlotOperationAttemptId, token);
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            await fixture.RequestLoadCorrectionOnConnectionAsync(
                connection,
                "70000000-0000-4000-8000-000000000041",
                SecondDemandId,
                load.SlotOperationAttemptId,
                System.Text.Json.JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!);
        }
        Assert.Contains("LoadCorrectionCommand", await fixture.OutboxTypesAsync());

        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow journey = await JourneyOfAsync(fixture, SecondDemandId);
        Assert.Equal("LOAD_CORRECTION_IN_PROGRESS", journey.BlockReasonCode);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, journey.Stage);
        Assert.Equal(checksBeforeTheCorrection, await DepartureChecksSentAsync(fixture));
    }

    /// <summary>
    /// 反过来：已经卸掉、不在车上的需求上开着一条装货纠错，不挡离站（调度追问的边界，审查返工）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 纠错的授权只核对装货已提交、仓位是它的子集、旅程在离站等待里，不看这条需求还在不在车上（那个口子归 cs#287）。所以车停在
    /// 后面某一站等离站时，能对一条早已卸掉的需求开出纠错；它要靠车载端对一排已经空了的仓位执行完才关，未必关得掉。离站判定若把
    /// 它算进去，车就一直停在离站等待里、没有人能解开。所以只看还在车上的归属（<c>LOADING</c>、<c>LOADED</c>）。
    /// </para>
    /// <para>
    /// 「已卸」是直接写进归属行的：真实路径要一个「先卸一条、再去取另一条」的四停靠计划，那是插位规划器的事，不是这里要测的。
    /// 纠错本身走真实授权路径，所以这条用例同时证明了那个口子今天确实开着。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnOpenCorrectionOnADemandAlreadyUnloadedDoesNotHoldTheDeparture()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(
            fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)),
            fixture.Demand(SecondDemandId, SecondSublot, Now.AddMinutes(-9), area: "N1-2"));
        fixture.BoxCounts.Set(FirstSublot, 7);
        fixture.BoxCounts.Set(SecondSublot, 7);
        await TickAndRunAsync(fixture);
        await Batch7ThreeStopJourneyTests.AppendDemandAsync(fixture, SecondDemandId, SecondSublot, "N1-2", 13);

        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        fixture.Clock.Advance(TimeSpan.FromSeconds(15));
        await TickAndRunAsync(fixture);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        await ArriveAtCurrentStopAsync(fixture, SecondDemandId, "TO_GATE");
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        await SettleLoadAsync(fixture, SecondDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await JourneyOfAsync(fixture, SecondDemandId)).Stage);

        JourneyDemandRow first = await fixture.Context.Set<JourneyDemandRow>()
            .SingleAsync(row => row.DemandId == FirstDemandId, token);
        first.Status = JourneyDemandStatuses.Unloaded;
        await fixture.Context.SaveChangesAsync(token);
        fixture.Context.ChangeTracker.Clear();
        int checksBeforeTheCorrection = await DepartureChecksSentAsync(fixture);

        StationOperationRow load = await fixture.Context.StationOperations.AsNoTracking()
            .SingleAsync(row => row.SlotOperationAttemptId == first.LoadSlotOperationAttemptId, token);
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            await fixture.RequestLoadCorrectionOnConnectionAsync(
                connection,
                "70000000-0000-4000-8000-000000000051",
                FirstDemandId,
                load.SlotOperationAttemptId,
                System.Text.Json.JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!);
        }
        // 前提：纠错确实被授权了（授权一侧的口子今天开着）。没被授权的话，下面「没挡住」就什么也证明不了。
        Assert.Contains("LoadCorrectionCommand", await fixture.OutboxTypesAsync());

        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow journey = await JourneyOfAsync(fixture, SecondDemandId);
        Assert.NotEqual("LOAD_CORRECTION_IN_PROGRESS", journey.BlockReasonCode);
        Assert.True(await DepartureChecksSentAsync(fixture) > checksBeforeTheCorrection,
            "An open correction on a demand no longer on the vehicle held the departure.");
    }

    /// <summary>
    /// 别的旅程留下的纠错不挡这一趟（审查必修 3）：同一个需求号上一条没结的纠错，若不是这辆车开的、或者开在这条需求加入本旅程之前，
    /// 它属于另一趟（7-10 改派之后，需求会带着它来到新车上）。
    /// </summary>
    /// <remarks>
    /// 纠错行上没有旅程号，只有车号、需求号、创建时刻。「属于本旅程」因此按两样认：本车开的，而且不早于这条需求加入本旅程。
    /// 两格各只差一样。正向（本旅程自己开的纠错挡住离站）由 <c>AYieldingVehicleWithACorrectionOpenIsNotAskedToLeave</c> 与
    /// <c>AnOpenCorrectionOnAnAppendedDemandHoldsTheDeparture</c> 守着，那两条走的是真实授权路径。
    /// </remarks>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [InlineData("opened on another vehicle", "AGV-PREVIOUS", 0)]
    [InlineData("opened before the demand joined this journey", null, -60)]
    public async Task AnOpenCorrectionLeftByAnotherJourneyDoesNotHoldTheDeparture(string why, string? agvId, int minutesFromJoining)
    {
        _ = why;
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        JourneyDemandRow membership = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == FirstDemandId, token);
        DateTimeOffset openedAt = minutesFromJoining == 0
            ? fixture.Clock.GetUtcNow()
            : membership.AddedAt.AddMinutes(minutesFromJoining);
        fixture.Context.RecoveryWorkflows.Add(new RecoveryWorkflowRow
        {
            WorkflowId = "70000000-0000-4000-8000-000000000061",
            WorkflowType = "LOAD_CORRECTION",
            AgvId = agvId ?? fixture.Options.AgvId,
            DemandId = FirstDemandId,
            SlotOperationAttemptId = membership.LoadSlotOperationAttemptId,
            SlotsJson = membership.TargetSlotsJson,
            State = RecoveryWorkflowState.CommandPending,
            RequestMessageId = "70000000-0000-4000-8000-000000000062",
            RequestContentHash = new string('f', 64),
            CreatedAt = openedAt,
            UpdatedAt = openedAt
        });
        await fixture.Context.SaveChangesAsync(token);
        fixture.Context.ChangeTracker.Clear();

        fixture.Clock.Advance(TimeSpan.FromSeconds(15));
        await TickAndRunAsync(fixture);

        Assert.NotEqual("LOAD_CORRECTION_IN_PROGRESS", (await JourneyOfAsync(fixture, FirstDemandId)).BlockReasonCode);
        Assert.True(await DepartureCheckSentAsync(fixture), "A correction left by another journey held this one's departure.");
    }

    /// <summary>
    /// 等单车断联期间被触发、服务端随后重启：重连之后车收到的快照是 <c>WAITING_STATION_YIELD</c>，不回退成等单；断联期间不发
    /// 离站核验，重连后才发（重放与重连、断联两格）。
    /// </summary>
    /// <remarks>
    /// 重启用新的引擎实例、清空变更跟踪器，读的只有落库的那一行：触发要是只活在内存里，这一步就把它丢了。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AYieldTriggeredWhileTheHolderWasOffAirSurvivesARestartAndIsWhatItSeesOnReconnect()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        await LoadTheFirstDemandAsync(fixture);
        await fixture.DropOnboardSessionAsync();
        await TickAndRunAsync(fixture);

        DateTimeOffset acceptedAt = fixture.Clock.GetUtcNow();
        await AcceptComerAsync(fixture.Context, await HolderStationAsync(fixture), acceptedAt);
        await TickAndRunAsync(fixture);
        Assert.Equal(acceptedAt, (await JourneyOfAsync(fixture, FirstDemandId)).YieldTriggeredAt);
        Assert.False(await DepartureCheckSentAsync(fixture), "An off-air vehicle was asked to leave.");

        await fixture.RecreateEngineAsync();
        await fixture.ReconnectAsync(2);
        await fixture.AdvanceSessionAsync(2);
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow holder = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(LoadingClosedReasons.WaitingStationYield, holder.LoadingClosedReason);
        LoadingPhaseSnapshot[] snapshots = await LoadingPhaseSnapshotsAsync(fixture);
        Assert.Equal((LoadingPhaseStates.Closed, LoadingClosedReasons.WaitingStationYield), (snapshots[^1].State, snapshots[^1].ClosedReason));
        Assert.DoesNotContain(snapshots.SkipWhile(snapshot => snapshot.State != LoadingPhaseStates.Closed),
            snapshot => snapshot.State != LoadingPhaseStates.Closed);
        Assert.True(await DepartureCheckSentAsync(fixture), "The vehicle came back and was never asked to leave.");
    }

    /// <summary>
    /// 持货期限与让站谁先到算谁（本票定的边界，<c>YieldTriggeredBeforeHoldingDeadline</c>）。等单车断联时推进段不判装货阶段，
    /// 两件事可能在它回来的同一轮一起被看到：触发早于期限，结束原因是让站；期限先到、触发在后，结束原因是持货超时。
    /// </summary>
    /// <remarks>
    /// 期限 30 分钟。两格只差触发落在期限之前还是之后，重连都在期限之后——所以绿不可能来自「期限还没到」。
    /// </remarks>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [InlineData(29, LoadingClosedReasons.WaitingStationYield)]
    [InlineData(31, LoadingClosedReasons.CargoHoldingTimeout)]
    public async Task WhicheverOfTheDeadlineAndTheYieldCameFirstIsWhyTheLoadingPhaseClosed(int triggerAfterMinutes, string reason)
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        DateTimeOffset closure = await LoadTheFirstDemandAsync(fixture);
        await fixture.DropOnboardSessionAsync();
        await TickAndRunAsync(fixture);

        fixture.Clock.Advance(closure + TimeSpan.FromMinutes(triggerAfterMinutes) - fixture.Clock.GetUtcNow());
        await AcceptComerAsync(fixture.Context, await HolderStationAsync(fixture), fixture.Clock.GetUtcNow());
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await fixture.ReconnectAsync(2);
        await fixture.AdvanceSessionAsync(2);
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow holder = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(LoadingPhaseStates.Closed, holder.LoadingPhaseState);
        Assert.Equal(reason, holder.LoadingClosedReason);
        Assert.Equal(ComerKey, holder.YieldTriggeredByVehicleKey);
    }

    /// <summary>
    /// 别的车先被承诺、这辆车后才开始等单：承诺那一刻它还在装，承诺方的事务不标记它。它自己那一轮补判——判出来本该是等单，
    /// 而别的车正以这个站为下一停靠，于是直接让站，车上一张 <c>CARGO_HOLDING_WAIT</c> 都不收。
    /// </summary>
    /// <remarks>
    /// 没有这一处，这辆车会一直停在别的车要来的站上，直到持货超时：让站只在「承诺那一刻已在等」时发生，而那辆车已经在路上了。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AVehicleThatStartsHoldingAfterAnotherWasCommittedToItsStationYieldsWithoutWaiting()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);

        await AcceptComerAsync(fixture.Context, await HolderStationAsync(fixture), fixture.Clock.GetUtcNow());
        await TickAndRunAsync(fixture);
        Assert.Null((await JourneyOfAsync(fixture, FirstDemandId)).YieldTriggeredAt);

        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow holder = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(LoadingClosedReasons.WaitingStationYield, holder.LoadingClosedReason);
        Assert.Equal(ComerKey, holder.YieldTriggeredByVehicleKey);
        Assert.Equal(fixture.Clock.GetUtcNow(), holder.YieldTriggeredAt);
        Assert.Equal(
            [LoadingPhaseStates.Loading, LoadingPhaseStates.Closed],
            (await LoadingPhaseSnapshotsAsync(fixture)).Select(snapshot => snapshot.State));
        Assert.True(await DepartureCheckSentAsync(fixture));
    }

    /// <summary>
    /// 让站不可撤回：触发之后，触发方的计划没了（它的旅程结束了），被让的车照样是 <c>CLOSED/WAITING_STATION_YIELD</c>、照样离站，
    /// 不回到等单（票面第 5 条）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AYieldIsNotWithdrawnWhenTheTriggeringVehiclesPlanChanges()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        await LoadTheFirstDemandAsync(fixture);
        await AcceptComerAsync(fixture.Context, await HolderStationAsync(fixture), fixture.Clock.GetUtcNow());

        JourneyRuntimeRow comer = await fixture.Context.JourneyRuntimes
            .SingleAsync(row => row.DemandId == ComerDemand, TestContext.Current.CancellationToken);
        comer.Stage = JourneyRuntimeStage.Completed;
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await TickAndRunAsync(fixture);
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow holder = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(LoadingPhaseStates.Closed, holder.LoadingPhaseState);
        Assert.Equal(LoadingClosedReasons.WaitingStationYield, holder.LoadingClosedReason);
        Assert.DoesNotContain(
            (await LoadingPhaseSnapshotsAsync(fixture)).SkipWhile(snapshot => snapshot.State != LoadingPhaseStates.Closed),
            snapshot => snapshot.State != LoadingPhaseStates.Closed);
        Assert.True(await DepartureCheckSentAsync(fixture));
    }

    /// <summary>
    /// 第二种「被承诺」：别的车离开上一站，下一停靠变成这个站（装或卸都算，这里是卸货站）。触发与那个停靠的完成落在同一次保存里。
    /// </summary>
    /// <remarks>
    /// 这里夹具那辆会跑的车是承诺方：它装完、离开取货站，下一停靠是它的卸货站。等单的是另一辆车的旅程，取货站就设在那个站上。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AVehicleLeavingForTheStationMakesTheHolderThereYield()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await SeedHolderAtTheGateAsync(fixture);
        await DriveToDepartureSafetyAsync(fixture);
        Assert.Null((await HolderAsync(fixture)).YieldTriggeredAt);

        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);

        JourneyRuntimeRow holder = await HolderAsync(fixture);
        Assert.Equal(fixture.Clock.GetUtcNow(), holder.YieldTriggeredAt);
        Assert.Equal(fixture.Options.VehicleKey, holder.YieldTriggeredByVehicleKey);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));
    }

    /// <summary>
    /// 离站那一次保存崩在中途：停靠没完成，触发也没落；服务端重启再跑一轮，两样一起落（崩溃点，推进那一路）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ADepartureThatCrashesBeforeItsSaveLeavesNoTriggerAndTheRetryWritesBoth()
    {
        FailOnceOnStopCompletion crash = new();
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: crash);
        await SeedHolderAtTheGateAsync(fixture);
        await DriveToDepartureSafetyAsync(fixture);

        JourneyStopRow stop = await CurrentStopAsync(fixture, FirstDemandId);
        await AddInboxAsync(
            fixture,
            FirstSafetyResultId,
            "PreDepartureSafetyCheckResult",
            SafeDepartureAnswer(fixture, stop.DepartureSafetyCheckId!, 7),
            stop.DepartureSafetyCheckMessageId);
        crash.Armed = true;
        await TickAndRunExpectingCrashAsync(fixture);
        Assert.True(crash.Fired, "The injected failure never fired, so this proved nothing.");

        await fixture.RecreateEngineAsync();
        Assert.Null((await HolderAsync(fixture)).YieldTriggeredAt);
        Assert.NotEqual(JourneyStopStatuses.Completed, (await StopAsync(fixture, stop.StopId)).Status);

        // 重跑在崩溃的同一时刻，不拨钟：离站路在授权移动时按「此刻」写订单意图的 CreatedAt，崩在授权之后、停靠完成之前，
        // 晚一秒重跑会被 AuthorizeMovementAsync 判成「同一身份、内容不同」而每轮都抛。那是离站路自己的崩溃恢复缺陷，
        // 与让站无关（本票发现，归 cs#291 的 U4）；这条用例只证触发与停靠完成同生共死，所以避开它。同一时刻重跑不是业务要求：
        // cs#291 修好之后，这里应改回拨钟重跑。
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JourneyStopStatuses.Completed, (await StopAsync(fixture, stop.StopId)).Status);
        Assert.Equal(fixture.Options.VehicleKey, (await HolderAsync(fixture)).YieldTriggeredByVehicleKey);
    }

    private static async Task<int> HolderStationAsync(RuntimeFixture fixture) =>
        (await CurrentStopAsync(fixture, FirstDemandId)).StationRiotId;

    /// <summary>另一辆车受理一条需求，取货站是 <paramref name="pickupStation"/>——它的下一停靠。</summary>
    private static async Task AcceptComerAsync(ControlServerDbContext context, int pickupStation, DateTimeOffset at)
    {
        JourneyExecutionPlan plan = Batch7JourneyFixture.Plan(ComerDemand, ComerAgv, ComerKey, at) with
        {
            PickupStationId = $"ST-{pickupStation}",
            PickupStationRiotId = pickupStation,
        };
        await new WireToGateStore(context).AcceptWithOrderIntentAsync(
            Batch7JourneyFixture.Snapshot(ComerDemand, at),
            JourneyPlanBuilder.PickupIntent(plan, ComerDemand, at),
            plan,
            TestContext.Current.CancellationToken);
    }

    /// <summary>另一辆车的旅程，站在取货停靠上持货等单，取货站就是夹具那辆车的卸货站。</summary>
    private static async Task SeedHolderAtTheGateAsync(RuntimeFixture fixture)
    {
        DateTimeOffset at = fixture.Clock.GetUtcNow();
        JourneyExecutionPlan plan = Batch7JourneyFixture.Plan(HolderDemand, HolderAgv, HolderKey, at) with
        {
            PickupStationId = "ST-GATE-AS-PICKUP",
            PickupStationRiotId = TaskTypeStationRuntimeSeed.GateStationRiotId,
        };
        await using ControlServerDbContext context = fixture.OpenConnectionContext();
        await new WireToGateStore(context).AcceptWithOrderIntentAsync(
            Batch7JourneyFixture.Snapshot(HolderDemand, at),
            JourneyPlanBuilder.PickupIntent(plan, HolderDemand, at),
            plan,
            TestContext.Current.CancellationToken);
        JourneyRuntimeRow holder = await context.JourneyRuntimes
            .SingleAsync(row => row.DemandId == HolderDemand, TestContext.Current.CancellationToken);
        holder.Stage = JourneyRuntimeStage.AwaitingStationDeparture;
        holder.LoadingPhaseState = LoadingPhaseStates.CargoHoldingWait;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task DriveToDepartureSafetyAsync(RuntimeFixture fixture)
    {
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
    }

    private static Task<JourneyRuntimeRow> HolderAsync(RuntimeFixture fixture) =>
        fixture.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.DemandId == HolderDemand, TestContext.Current.CancellationToken);

    private static Task<JourneyStopRow> StopAsync(RuntimeFixture fixture, string stopId) =>
        fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(row => row.StopId == stopId, TestContext.Current.CancellationToken);

    private static object UnsafeDepartureAnswer(RuntimeFixture fixture, string checkId, bool slotsLocked, bool unknownPresent) => new
    {
        preDepartureSafetyCheckId = checkId,
        outcome = "UNSAFE",
        observedAt = fixture.Clock.GetUtcNow(),
        safetyStateVersion = 7L,
        validUntil = fixture.Clock.GetUtcNow().AddMinutes(1),
        safety = new
        {
            departureSafe = false,
            vehicleStopped = true,
            allTargetSlotsLocked = slotsLocked,
            allUnlockOutputsReset = true,
            unknownPresent,
            reasonCodes = new[] { unknownPresent ? "SAFETY_STATE_UNKNOWN" : "SLOT_DOOR_NOT_LOCKED" }
        }
    };

    /// <summary>把某个停靠标记为完成的那一条 <c>UPDATE</c> 抛一次，如同离站那一次保存写到一半时崩了。</summary>
    private sealed class FailOnceOnStopCompletion : DbCommandInterceptor
    {
        public bool Armed { get; set; }

        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Check(DbCommand command)
        {
            if (!Armed || Fired ||
                !command.CommandText.Contains("UPDATE \"JourneyStops\"", StringComparison.Ordinal) ||
                !command.Parameters.Cast<DbParameter>().Any(parameter => Equals(parameter.Value, JourneyStopStatuses.Completed)))
            {
                return;
            }

            Fired = true;
            throw new InvalidOperationException("Injected failure while completing the departed stop.");
        }
    }
}
