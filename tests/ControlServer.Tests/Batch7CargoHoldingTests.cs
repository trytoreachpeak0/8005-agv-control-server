using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 持货等单、按侧装满、持货超时在推进段上的样子（批次7-07，control-server#212；REQ-0354、ADR-cross-0057／0059）。
/// </summary>
/// <remarks>
/// <para>
/// <b>判据断到车上看得见的那一层。</b>装货阶段的列是服务端自己的记录，车载端看到的是
/// <c>VehicleBusinessStateSnapshot.loadingPhase</c>；两者可以不一致（列写了、快照没发），而那正是票面「崩溃点」要防的。
/// 所以每条用例都读发件箱里的快照，不只读列。
/// </para>
/// <para>
/// <b>持货等单适用与否靠每区参数</b>：夹具默认一版都没有，也就是「参数批准之前」——批次 7 之前的全部既有用例都在这个前提下，
/// 这里的用例各自写一版进去。
/// </para>
/// </remarks>
public sealed class Batch7CargoHoldingTests
{
    private const string SecondPickupArea = "N1-2";
    private const int SecondPickupStationRiotId = 13;

    /// <summary>
    /// 装完当前计划、两侧都还有空，车留在最后装货的站点等单，不发离站核验（REQ-0354「此前车辆持货等单」）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>正事实与负事实各一条，缺一条都会假绿。</b>只断「没有离站核验」，一辆坏掉的、什么都不做的车也能通过；只断
    /// 「出现了 CARGO_HOLDING_WAIT」，一个发了快照却照样离站的实现也能通过。
    /// </para>
    /// <para>
    /// 期限等于第一个 LoadBatch 闭环那一轮的服务端时刻加 30 分钟——起算点取服务端的钟，见
    /// <c>JourneyRuntimeEngine</c> 装货落定那一段的注释。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AVehicleWithRoomLeftHoldsAtItsLastPickupInsteadOfLeaving()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        DateTimeOffset closure = await LoadTheFirstDemandAsync(fixture);

        JourneyRuntimeRow runtime = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, runtime.Stage);
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, runtime.LoadingPhaseState);
        Assert.Equal(closure, runtime.CargoHoldingStartedAt);
        Assert.False(await DepartureCheckSentAsync(fixture), "A holding vehicle was asked for departure safety.");

        LoadingPhaseSnapshot[] snapshots = await LoadingPhaseSnapshotsAsync(fixture);
        Assert.Equal(
            [
                new LoadingPhaseSnapshot(LoadingPhaseStates.Loading, null, null),
                new LoadingPhaseSnapshot(LoadingPhaseStates.CargoHoldingWait, closure + TimeSpan.FromMinutes(30), null),
            ],
            snapshots.Select(snapshot => snapshot with { Revision = 0 }));
        AssertStrictlyIncreasing(snapshots);

        // 等着的每一轮都不再发：快照只在变了的时候发。
        for (int round = 0; round < 3; round++)
        {
            await TickAndRunAsync(fixture);
        }
        Assert.Equal(2, (await LoadingPhaseSnapshotsAsync(fixture)).Length);
        Assert.False(await DepartureCheckSentAsync(fixture));
    }

    /// <summary>
    /// 持货期限到，装货阶段以 <c>CARGO_HOLDING_TIMEOUT</c> 结束，车随即去卸货；卸货到站那一张快照报的也是这个原因。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheHoldingDeadlineEndsTheLoadingPhaseAndTheVehicleLeaves()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        await LoadTheFirstDemandAsync(fixture);

        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow runtime = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(LoadingPhaseStates.Closed, runtime.LoadingPhaseState);
        Assert.Equal(LoadingClosedReasons.CargoHoldingTimeout, runtime.LoadingClosedReason);
        Assert.True(await DepartureCheckSentAsync(fixture), "The deadline passed and the vehicle was never asked to leave.");

        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_GATE");

        LoadingPhaseSnapshot[] snapshots = await LoadingPhaseSnapshotsAsync(fixture);
        Assert.Equal(
            [
                LoadingPhaseStates.Loading,
                LoadingPhaseStates.CargoHoldingWait,
                LoadingPhaseStates.Closed,
                LoadingPhaseStates.Closed,
            ],
            snapshots.Select(snapshot => snapshot.State));
        Assert.All(snapshots[2..], snapshot => Assert.Equal(LoadingClosedReasons.CargoHoldingTimeout, snapshot.ClosedReason));
        // 关闭之后期限照旧带着（program#94：「进入 CLOSED 后保留原值、不清空」），与 WAIT 那张是同一个时刻。
        Assert.NotNull(snapshots[1].CargoHoldingDeadlineAt);
        Assert.All(snapshots[2..], snapshot => Assert.Equal(snapshots[1].CargoHoldingDeadlineAt, snapshot.CargoHoldingDeadlineAt));
        AssertStrictlyIncreasing(snapshots);
    }

    /// <summary>
    /// 两侧都满（这一辆的前侧装着它自己的货，后侧全禁用）：不等单，装完即走，离开时以 <c>VEHICLE_FULL</c> 结束。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>满在装货之前就成立</b>：满按「已装或已预留」的货算，受理那一刻目标仓位就为这条需求留着了。所以第一张装货阶段
    /// 快照发在车还在去取货站的路上——那一张用的是「车在路上」那条修订号算式，下一张到站快照比它大一。这条用例因此同时
    /// 钉着那条算式：算错一格，到站那一张就与它撞号。
    /// </para>
    /// <para>
    /// 后侧用禁用仓位造满，是因为「该分组已无空仓」这一半今天只有这一种构造法；「只因本车货物占侧的候选」那一半要一轮
    /// 真派车，在 L2 场景 <c>cargo-holding-side-full</c> 里取证。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AFullVehicleDoesNotWaitAndClosesAsFullWhenItLeaves()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await TickAndRunAsync(fixture);
        await LeaveOnlyTheFirstDemandsSlotsAsync(fixture);

        await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_PICKUP");
        Assert.Equal(LoadingPhaseStates.VehicleFull, (await JourneyOfAsync(fixture, FirstDemandId)).LoadingPhaseState);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        Assert.True(await DepartureCheckSentAsync(fixture), "A full vehicle waited for more demands.");

        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        JourneyRuntimeRow departed = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(LoadingPhaseStates.Closed, departed.LoadingPhaseState);
        Assert.Equal(LoadingClosedReasons.VehicleFull, departed.LoadingClosedReason);
        await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_GATE");

        LoadingPhaseSnapshot[] snapshots = await LoadingPhaseSnapshotsAsync(fixture);
        Assert.Equal(
            [
                (LoadingPhaseStates.VehicleFull, (string?)null),
                (LoadingPhaseStates.VehicleFull, null),
                (LoadingPhaseStates.Closed, LoadingClosedReasons.VehicleFull),
                (LoadingPhaseStates.Closed, LoadingClosedReasons.VehicleFull),
            ],
            snapshots.Select(snapshot => (snapshot.State, snapshot.ClosedReason)));
        AssertStrictlyIncreasing(snapshots);
    }

    /// <summary>
    /// 持货起算点断联不重置、重启也不重置；同一时刻站点离站等待那一列在断联时作废——两个期限不共用一个字段
    /// （票面第 5 条，ADR-cross-0055 与 0057）。
    /// </summary>
    /// <remarks>
    /// 重启用一个新的引擎实例、清空变更跟踪器，读的只有落库的那一行：起算点要是只活在内存里，这一步就把它丢了。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheHoldingClockSurvivesADisconnectAndARestartWhileTheStationWaitIsVoided()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromMinutes(5);
        DateTimeOffset closure = await LoadTheFirstDemandAsync(fixture);
        JourneyRuntimeRow holding = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.NotNull(holding.StationDepartureWaitStartedAt);

        await fixture.DropOnboardSessionAsync();
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow offAir = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Null(offAir.StationDepartureWaitStartedAt);
        Assert.Equal(closure, offAir.CargoHoldingStartedAt);

        await fixture.ReconnectAsync(2);
        await fixture.AdvanceSessionAsync(2);
        await fixture.RecreateEngineAsync();
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow back = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(closure, back.CargoHoldingStartedAt);
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, back.LoadingPhaseState);
        Assert.Equal(
            closure + TimeSpan.FromMinutes(30),
            (await LoadingPhaseSnapshotsAsync(fixture))[^1].CargoHoldingDeadlineAt);
    }

    /// <summary>
    /// 持货期间追加进一条需求：车离开去装它，状态回到 LOADING；起算点不因新停靠重置；到期时那一批正在执行就等它闭环，
    /// 闭环后立刻结束（ADR-cross-0057「到期不打断正在进行的仓位操作」）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnAppendWhileHoldingLoadsItAndTheDeadlineWaitsForThatBatchToClose()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        DateTimeOffset closure = await LoadTheFirstDemandAsync(fixture);

        await AppendTheSecondDemandAsync(fixture);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow leaving = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(LoadingPhaseStates.Loading, leaving.LoadingPhaseState);
        Assert.True(await DepartureCheckSentAsync(fixture), "The append did not send the holding vehicle on.");

        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        await ArriveAtCurrentStopAsync(fixture, SecondDemandId, "TO_GATE");
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await JourneyOfAsync(fixture, SecondDemandId)).Stage);

        // 期限在这一批执行期间到：不结束。
        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow midBatch = await JourneyOfAsync(fixture, SecondDemandId);
        Assert.Equal(LoadingPhaseStates.Loading, midBatch.LoadingPhaseState);
        Assert.Equal(closure, midBatch.CargoHoldingStartedAt);

        // 闭环的那一轮就结束，车随即离站。
        int checksBefore = await DepartureChecksSentAsync(fixture);
        await SettleLoadAsync(fixture, SecondDemandId);
        JourneyRuntimeRow closed = await JourneyOfAsync(fixture, SecondDemandId);
        Assert.Equal(LoadingPhaseStates.Closed, closed.LoadingPhaseState);
        Assert.Equal(LoadingClosedReasons.CargoHoldingTimeout, closed.LoadingClosedReason);
        Assert.Equal(checksBefore + 1, await DepartureChecksSentAsync(fixture));
    }

    /// <summary>
    /// 持货期间那一批的结果需要恢复：持货期限照常过去，但车被阻断，不发离站核验（票面「FAILED、UNKNOWN 与异常分支」）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ABatchThatNeedsRecoveryHoldsTheVehicleThoughTheDeadlinePassed()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        await LoadTheFirstDemandAsync(fixture);
        await AppendTheSecondDemandAsync(fixture);
        await TickAndRunAsync(fixture);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        await ArriveAtCurrentStopAsync(fixture, SecondDemandId, "TO_GATE");
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        int checksBefore = await DepartureChecksSentAsync(fixture);

        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        StationOperationRow load = await fixture.Context.StationOperations.AsNoTracking()
            .SingleAsync(row => row.DemandId == SecondDemandId && row.OperationType == SlotOperationType.Load,
                TestContext.Current.CancellationToken);
        await fixture.ApplyTimedOutResultAsync(load, SlotOperationType.Load);
        for (int round = 0; round < 3; round++)
        {
            await TickAndRunAsync(fixture);
        }

        Assert.Equal(JourneyRuntimeStage.Blocked, (await JourneyOfAsync(fixture, SecondDemandId)).Stage);
        Assert.Equal(checksBefore, await DepartureChecksSentAsync(fixture));
    }

    /// <summary>
    /// 装货阶段结束之后，追加事务本身拒绝（票面「追加受理与离站请求在同一串行轮里——『是否已离开』在写事务里读」）。
    /// </summary>
    /// <remarks>
    /// 在途链上 <c>LoadingPhaseOpenCriterion</c> 挡的是轮次开始时读到的计划；这一条挡的是写入那一刻的旅程行。
    /// 直接走落库那一条路，是因为它要证明的恰恰是「轮次那一侧放过去了也写不进去」。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnAppendAfterTheLoadingPhaseClosedIsRefusedInsideItsWriteTransaction()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        await LoadTheFirstDemandAsync(fixture);
        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await TickAndRunAsync(fixture);
        Assert.Equal(LoadingPhaseStates.Closed, (await JourneyOfAsync(fixture, FirstDemandId)).LoadingPhaseState);

        BusinessIdentityConflictException refused = await Assert.ThrowsAsync<BusinessIdentityConflictException>(
            () => AppendTheSecondDemandAsync(fixture));
        Assert.Contains("loading phase", refused.Message, StringComparison.Ordinal);
        Assert.False(await fixture.Context.AcceptedDemands.AsNoTracking()
            .AnyAsync(row => row.DemandId == SecondDemandId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 状态与快照同一次保存、先持久化后发送（票面「崩溃点」）：发 WAIT 那一张时对端断了，库里状态与发件箱行都在；
    /// 下一轮不重复发一张新的，由重放补发那一行。
    /// </summary>
    /// <remarks>
    /// 反过来的实现——先存状态、再发快照——崩在两者之间就是「库里已经 WAIT、车上永远收不到」：下一轮读到的状态已经是
    /// WAIT，没有变化，不会再发。这条用例的第二个断言就是为它写的。
    /// <para>
    /// 两种起点各跑一遍：按车计数器从零起，与预置到 100（「这辆车已经跑过几十趟」）。补发只认 <c>RuntimeMessageIds</c> 里的
    /// id，而 cs#285 那个缺陷正是计划重发版的 id 按固定区间 1..18 枚举、号一大就不在集合里。装货阶段快照的 id 由当前基准
    /// 算出，不枚举区间；高位这一行守的就是「以后有人把它也写成一个区间」。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheStateAndItsSnapshotAreSavedTogetherBeforeTheSnapshotIsSent(bool highRevisions)
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        if (highRevisions)
        {
            fixture.Context.Set<VehicleSnapshotRevisionRow>().Add(new VehicleSnapshotRevisionRow
            {
                AgvId = fixture.Options.AgvId,
                VehicleBusinessRevision = 100,
                WorklistRevision = 100,
                PlanRevision = 100,
            });
            await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            fixture.Context.ChangeTracker.Clear();
        }

        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);

        bool cut = false;
        fixture.Peer.OnMessageSent = line =>
        {
            if (!cut && line.Contains("\"CARGO_HOLDING_WAIT\"", StringComparison.Ordinal))
            {
                cut = true;
                throw new IOException("peer dropped mid-send");
            }
            return Task.CompletedTask;
        };
        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunExpectingCrashAsync(fixture);
        Assert.True(cut);

        fixture.Context.ChangeTracker.Clear();
        JourneyRuntimeRow afterCrash = await JourneyOfAsync(fixture, FirstDemandId);
        ProtocolOutboxRow[] waits = await WaitSnapshotRowsAsync(fixture);
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, afterCrash.LoadingPhaseState);
        Assert.Single(waits);
        long waitRevision;
        using (JsonDocument document = JsonDocument.Parse(waits[0].PayloadJson))
        {
            waitRevision = document.RootElement.GetProperty("payload").GetProperty("vehicleBusinessStateRevision").GetInt64();
        }

        Assert.True(
            highRevisions ? waitRevision > 100 : waitRevision < 18,
            $"The fixture did not reach the revisions this row is about: {waitRevision}.");

        fixture.Peer.OnMessageSent = null;
        int sentBefore = fixture.Peer.Lines.Count;
        await fixture.RecreateEngineAsync();
        await TickAndRunAsync(fixture);
        Assert.Single(await WaitSnapshotRowsAsync(fixture));
        Assert.Contains(
            fixture.Peer.Lines.Skip(sentBefore).Select(line => System.Text.Encoding.UTF8.GetString(line)),
            line => line.Contains(waits[0].MessageId, StringComparison.Ordinal));
    }

    /// <summary>
    /// 车在路上判满：那张快照的号比下一站到站那张小一，断线之后照样补发（审查 M4）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 车在两个停靠之间时发的装货阶段快照，号按「还没到站」算，是 <c>StopRevision(基准, 下一站) - 1</c>；补发集合
    /// <c>RuntimeMessageIds</c> 里专门有一行给它。只删那一行时 Batch7* 的其余用例全绿——到站那一张另有一行，而别的用例里
    /// 车在路上发的快照都在断线之前就确认了。这条用例让那一张恰好在发送时断线，它唯一的出路就是那一行。
    /// </para>
    /// <para>
    /// 判满来自读口（与 <see cref="OwnCargoCandidatesOnBothSidesMakeTheWaitingVehicleFullAndItLeaves"/> 同一个做法）：车已经在
    /// 去第二个取货停靠的路上，两侧都有「只因本车货物装不下」的候选，于是在路上就是 <c>VEHICLE_FULL</c>（规则 6：满先于有待装）。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ASnapshotSentBetweenStopsIsReplayedAfterTheSessionDropsMidSend()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        await LoadTheFirstDemandAsync(fixture);
        await AppendTheSecondDemandAsync(fixture);
        await TickAndRunAsync(fixture);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        JourneyRuntimeRow underWay = await JourneyOfAsync(fixture, SecondDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, underWay.Stage);

        bool cut = false;
        fixture.Peer.OnMessageSent = line =>
        {
            if (!cut && line.Contains("\"VEHICLE_FULL\"", StringComparison.Ordinal))
            {
                cut = true;
                throw new IOException("peer dropped mid-send");
            }
            return Task.CompletedTask;
        };
        RecordRound(fixture, (DispatchReasonCodes.SlotGroupOccupiedByOwnCargo, "FRONT"),
            (DispatchReasonCodes.SlotGroupOccupiedByOwnCargo, "REAR"));
        await TickAndRunExpectingCrashAsync(fixture);
        Assert.True(cut, "The vehicle never became full between stops, so there was nothing to cut.");

        fixture.Context.ChangeTracker.Clear();
        JourneyRuntimeRow afterCrash = await JourneyOfAsync(fixture, SecondDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, afterCrash.Stage);
        Assert.Equal(LoadingPhaseStates.VehicleFull, afterCrash.LoadingPhaseState);
        ProtocolOutboxRow fullSnapshot = (await fixture.Context.ProtocolOutbox.AsNoTracking()
                .Where(row => row.MessageType == "VehicleBusinessStateSnapshot" && row.AcknowledgedAt == null)
                .ToArrayAsync(TestContext.Current.CancellationToken))
            .Single(row => row.PayloadJson.Contains("\"VEHICLE_FULL\"", StringComparison.Ordinal));

        fixture.Peer.OnMessageSent = null;
        int sentBefore = fixture.Peer.Lines.Count;
        await fixture.RecreateEngineAsync();
        await TickAndRunAsync(fixture);
        Assert.Contains(
            fixture.Peer.Lines.Skip(sentBefore).Select(line => System.Text.Encoding.UTF8.GetString(line)),
            line => line.Contains(fullSnapshot.MessageId, StringComparison.Ordinal));
    }

    /// <summary>
    /// 按侧装满（REQ-0354）：两侧各有一条「只因本车货物占侧而装不下」的候选，等单的车就满了，随即离站。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这里的「上一轮派车的裁决」是直接写进读口的，不是真跑一轮派车：要证的是推进段怎样把读口的结论与自己读账本得出的
    /// 「这一侧已无空仓」合成两侧各自的判定。读口里那个原因码只在「其余准入全过」时出现，那是链的次序保证的
    /// （<c>DispatchAdmissionChainDerivationTests.TheSlotCapacityCriterionRunsLastOnBothChains</c>），L2 场景
    /// <c>cargo-holding-side-full</c> 从真派车那一头取证。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task OwnCargoCandidatesOnBothSidesMakeTheWaitingVehicleFullAndItLeaves()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        await LoadTheFirstDemandAsync(fixture);
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, (await JourneyOfAsync(fixture, FirstDemandId)).LoadingPhaseState);

        RecordRound(fixture, (DispatchReasonCodes.SlotGroupOccupiedByOwnCargo, "FRONT"),
            (DispatchReasonCodes.SlotGroupOccupiedByOwnCargo, "REAR"));
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow full = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(LoadingPhaseStates.VehicleFull, full.LoadingPhaseState);
        Assert.Equal("[\"FRONT\",\"REAR\"]", full.FullSlotPositionsJson);
        Assert.True(await DepartureCheckSentAsync(fixture), "A vehicle full on both sides kept waiting.");
        Assert.Equal(LoadingPhaseStates.VehicleFull, (await LoadingPhaseSnapshotsAsync(fixture))[^1].State);
    }

    /// <summary>
    /// 只有一侧满，车照样等（REQ-0354「尚有分组未装满时……车辆处于 CargoHoldingWait」）；被别的理由挡住的候选
    /// ——超大需求、仓位禁用、不服务这个分区——一侧都不算满（ADR-cross-0059 拒绝过的那个选项）。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [InlineData("one side full of own cargo", DispatchReasonCodes.SlotGroupOccupiedByOwnCargo, "FRONT", null, null)]
    [InlineData("oversized on both sides", DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup, "FRONT",
        DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup, "REAR")]
    [InlineData("disabled slots on both sides", DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable, "FRONT",
        DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable, "REAR")]
    [InlineData("another gate on both sides", "VEHICLE_NOT_ADMITTED_IN_ZONE", "FRONT", "TASK_TYPE_HELD", "REAR")]
    [InlineData("own cargo on one side, oversized on the other", DispatchReasonCodes.SlotGroupOccupiedByOwnCargo, "FRONT",
        DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup, "REAR")]
    public async Task OnlyOwnCargoOnEverySideFillsTheVehicle(
        string row, string firstReason, string firstSide, string? secondReason, string? secondSide)
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        await LoadTheFirstDemandAsync(fixture);

        RecordRound(fixture, [(firstReason, firstSide), .. secondReason is null ? [] : new[] { (secondReason, secondSide!) }]);
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow runtime = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(
            runtime.LoadingPhaseState == LoadingPhaseStates.CargoHoldingWait,
            $"{row}: {runtime.LoadingPhaseState} (full sides {runtime.FullSlotPositionsJson})");
        Assert.False(await DepartureCheckSentAsync(fixture), $"{row}: the vehicle left.");
    }

    /// <summary>
    /// 重启之后、第一轮派车之前，读口是空的：装货阶段沿用落库的判定，不翻状态，不给车发两张来回翻的快照。
    /// </summary>
    /// <remarks>
    /// 离站等待开着（5 分钟），好让满了的车停在站上，这一轮的状态看得见。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AfterARestartTheLastFullnessJudgementStandsUntilARoundSaysOtherwise()
    {
        await using RuntimeFixture fixture = await HoldingFixtureAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromMinutes(5);
        await LoadTheFirstDemandAsync(fixture);
        RecordRound(fixture, (DispatchReasonCodes.SlotGroupOccupiedByOwnCargo, "FRONT"),
            (DispatchReasonCodes.SlotGroupOccupiedByOwnCargo, "REAR"));
        await TickAndRunAsync(fixture);
        Assert.Equal(LoadingPhaseStates.VehicleFull, (await JourneyOfAsync(fixture, FirstDemandId)).LoadingPhaseState);
        int snapshotsBefore = (await LoadingPhaseSnapshotsAsync(fixture)).Length;

        fixture.SlotGroupFullness = new();
        await fixture.RecreateEngineAsync();
        await TickAndRunAsync(fixture);

        Assert.Equal(LoadingPhaseStates.VehicleFull, (await JourneyOfAsync(fixture, FirstDemandId)).LoadingPhaseState);
        Assert.Equal(snapshotsBefore, (await LoadingPhaseSnapshotsAsync(fixture)).Length);

        // 第一轮派车说这辆车现在一侧都不满：那才改判。
        RecordRound(fixture);
        await TickAndRunAsync(fixture);
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, (await JourneyOfAsync(fixture, FirstDemandId)).LoadingPhaseState);
    }

    /// <summary>
    /// 本区参数配成 0 与没配置是同一件事（REQ-0198）：不持货等单，装完即走，与批次 7 之前相同（票面第 6 条）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AZeroAllowanceMeansNoHoldingLikeNoParameterAtAll()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await new DispatchZoneParameterStore(fixture.Context, CreateGovernedPublisher(fixture.Context))
            .WriteVersionAsync(
                [new DispatchZoneParameters(fixture.Options.DispatchZone, 0, null)],
                fixture.Clock.GetUtcNow(),
                TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        await LoadTheFirstDemandAsync(fixture);

        JourneyRuntimeRow runtime = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(LoadingPhaseStates.Closed, runtime.LoadingPhaseState);
        Assert.Equal(LoadingClosedReasons.PlannedLoadingComplete, runtime.LoadingClosedReason);
        Assert.True(await DepartureCheckSentAsync(fixture));
        Assert.DoesNotContain(
            await LoadingPhaseSnapshotsAsync(fixture),
            snapshot => snapshot.State == LoadingPhaseStates.CargoHoldingWait);
    }

    /// <summary>
    /// 状态与快照是同一次保存：写发件箱那一步失败，状态也不落库；重启之后从同一组事实再推一遍，那一张恰好发一次。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>它与上一条守的是两件不同的事。</b>上一条让发送失败——那时两样都已落库，守的是「先持久化后发送」。这一条让发件箱那一行
    /// 写不进去，守的是「状态与快照不分两次存」：分两次存的实现在这里留下「状态已是 WAIT、发件箱里没有 WAIT」，
    /// 下一轮读到状态没变、不会再发，车上永远收不到。
    /// </para>
    /// <para>
    /// 失败注在数据库命令上（插入 <c>ProtocolOutbox</c> 且载荷带 <c>CARGO_HOLDING_WAIT</c> 的那一条），只注一次。
    /// 注在别处——例如让发布方法抛——证明不了这件事：那样发件箱与状态都还没进 SaveChanges，谁先谁后看不出来。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task WhenTheSnapshotCannotBeStoredTheStateIsNotStoredEither()
    {
        FailOnceOnWaitSnapshotInsert failure = new();
        await using RuntimeFixture fixture = await HoldingFixtureAsync(failure);
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);

        failure.Armed = true;
        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunExpectingCrashAsync(fixture);
        Assert.True(failure.Fired);

        fixture.Context.ChangeTracker.Clear();
        Assert.NotEqual(LoadingPhaseStates.CargoHoldingWait, (await JourneyOfAsync(fixture, FirstDemandId)).LoadingPhaseState);
        Assert.Empty(await WaitSnapshotRowsAsync(fixture));

        await fixture.RecreateEngineAsync();
        await TickAndRunAsync(fixture);
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, (await JourneyOfAsync(fixture, FirstDemandId)).LoadingPhaseState);
        Assert.Single(await WaitSnapshotRowsAsync(fixture));
    }

    /// <summary>
    /// 不适用持货等单（参数批准之前的全部旅程）：发快照的时点与内容与批次 7 之前逐条相同（票面第 7 条）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>期望值由改动之前那个函数生成</b>，不是手写：<see cref="PreBatch7LoadingPhase"/> 是批次 7 之前
    /// <c>JourneyRuntimeEngine.LoadingPhase</c> 的原文，产品代码里它已经换成读列。票面要的就是这个对照——「用改动前的函数对
    /// 同一组阶段序列生成期望值」。两张快照发出时旅程各在哪个阶段，是改动前后都一样的事实：取货到站那一张在
    /// <c>AwaitingPickupArrival</c> 里发，卸货到站那一张在 <c>AwaitingGateArrival</c> 里发。
    /// </para>
    /// <para>
    /// 逐字节的那一半由 <c>Batch7StopDrivenAdvanceWireParityTests</c> 的零变化基线守着；这一条守的是「列与旧函数说同一件事」，
    /// 那几份基线只看得见结果，看不见这两者是不是碰巧相等。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task WithoutHoldingEverySnapshotIsTheOneTheStageDerivedMappingGave()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await RunJourneyToCompletionAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId, FirstSafetyResultId);

        LoadingPhaseSnapshot[] snapshots = await LoadingPhaseSnapshotsAsync(fixture);
        LoadingPhaseProjection[] expected =
        [
            PreBatch7LoadingPhase(JourneyRuntimeStage.AwaitingPickupArrival, loadBatchClosed: false),
            PreBatch7LoadingPhase(JourneyRuntimeStage.AwaitingGateArrival, loadBatchClosed: true),
        ];
        Assert.Equal(
            expected.Select(phase => new LoadingPhaseSnapshot(phase.State, phase.CargoHoldingDeadlineAt, phase.ClosedReason)),
            snapshots.Select(snapshot => snapshot with { Revision = 0 }));
        // LoadingPhaseSnapshotsAsync 跳过不带 loadingPhase 的业务状态（control-server#323）。跳过的只许是收尾那一张：
        // 恰好一张、号在这条流上最大。否则旅程中途误发一张不带旅程的业务状态，会被那个读取函数静默略过、这条用例照样绿。
        ClosureSnapshotAssertions.Snapshot skipped = Assert.Single(
            await ClosureSnapshotAssertions.SnapshotsAsync(fixture.Context, fixture.Options.AgvId),
            item => item.MessageType == "VehicleBusinessStateSnapshot" &&
                    item.Payload.GetProperty("loadingPhase").ValueKind == JsonValueKind.Null);
        Assert.True(skipped.Revision > snapshots.Max(snapshot => snapshot.Revision));

        JourneyRuntimeRow completed = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(LoadingPhaseStates.Closed, completed.LoadingPhaseState);
        Assert.Equal(LoadingClosedReasons.PlannedLoadingComplete, completed.LoadingClosedReason);
    }

    /// <summary>
    /// 批次 7 之前 <c>JourneyRuntimeEngine.LoadingPhase</c> 的原文，一字未改，只在这里当判据用。
    /// </summary>
    internal static LoadingPhaseProjection PreBatch7LoadingPhase(JourneyRuntimeStage stage, bool loadBatchClosed) => stage switch
    {
        JourneyRuntimeStage.AwaitingPickupArrival or
        JourneyRuntimeStage.AwaitingSublot or
        JourneyRuntimeStage.AwaitingLoadResult => LoadingPhaseProjection.Loading,
        JourneyRuntimeStage.AwaitingStationDeparture or
        JourneyRuntimeStage.AwaitingDepartureSafety or
        JourneyRuntimeStage.AwaitingGateArrival or
        JourneyRuntimeStage.AwaitingUnloadResult => LoadingPhaseProjection.PlannedLoadingComplete,
        JourneyRuntimeStage.Blocked or
        JourneyRuntimeStage.Completed => loadBatchClosed
            ? LoadingPhaseProjection.PlannedLoadingComplete
            : LoadingPhaseProjection.Loading,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Journey stage has no loading phase mapping.")
    };

    /// <summary>发给车的一张车辆业务状态快照里的 <c>loadingPhase</c>，连同它的修订号。</summary>
    internal sealed record LoadingPhaseSnapshot(string State, DateTimeOffset? CargoHoldingDeadlineAt, string? ClosedReason)
    {
        public long Revision { get; init; }
    }

    /// <summary>一个写好了每区参数、适用持货等单的夹具：本区允许途中追加，车能服务它。</summary>
    internal static async Task<RuntimeFixture> HoldingFixtureAsync(DbCommandInterceptor? commands = null)
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: commands);
        await new DispatchZoneParameterStore(fixture.Context, CreateGovernedPublisher(fixture.Context))
            .WriteVersionAsync(
                [new DispatchZoneParameters(fixture.Options.DispatchZone, 10_000_000, null)],
                fixture.Clock.GetUtcNow(),
                TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    /// <summary>第一条需求受理、到站、录入、装货落定；返回落定那一轮的服务端时刻，也就是持货起算点。</summary>
    internal static async Task<DateTimeOffset> LoadTheFirstDemandAsync(RuntimeFixture fixture)
    {
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);
        return fixture.Clock.GetUtcNow();
    }

    /// <summary>第二条需求追加进这趟旅程，取货在另一个站（13）。录入之后服务端按箱数重校验，所以箱数一并给上。</summary>
    private static Task AppendTheSecondDemandAsync(RuntimeFixture fixture)
    {
        fixture.BoxCounts.Set(SecondSublot, 7);
        return Batch7ThreeStopJourneyTests.AppendDemandAsync(
            fixture, SecondDemandId, SecondSublot, SecondPickupArea, SecondPickupStationRiotId);
    }

    /// <summary>把第一条需求目标仓位之外的每个仓位都禁用：前侧只剩它自己的货，后侧一个空仓都没有。</summary>
    internal static async Task LeaveOnlyTheFirstDemandsSlotsAsync(RuntimeFixture fixture)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        int[] own = JsonSerializer.Deserialize<int[]>((await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == FirstDemandId, token)).TargetSlotsJson)!;
        ProtocolInboxRow[] rows = await fixture.Context.ProtocolInbox
            .Where(row => row.MessageType == "CapabilitySnapshot" || row.MessageType == "SafetyStateSnapshot")
            .ToArrayAsync(token);
        foreach (ProtocolInboxRow row in rows)
        {
            JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
            foreach (JsonNode? slot in root["payload"]!["slotStates"]!.AsArray())
            {
                if (!own.Contains(slot!["slotNo"]!.GetValue<int>()))
                {
                    slot["administrativeAvailability"] = "DISABLED";
                }
            }
            row.RequestJson = root.ToJsonString();
        }
        await fixture.Context.SaveChangesAsync(token);
        fixture.Context.ChangeTracker.Clear();
    }

    internal static async Task<LoadingPhaseSnapshot[]> LoadingPhaseSnapshotsAsync(RuntimeFixture fixture)
    {
        ProtocolOutboxRow[] rows = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "VehicleBusinessStateSnapshot")
            .ToArrayAsync(TestContext.Current.CancellationToken);
        return
        [
            .. rows.Select(row =>
                {
                    using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
                    JsonElement payload = document.RootElement.GetProperty("payload");
                    JsonElement phase = payload.GetProperty("loadingPhase");
                    // 不带 loadingPhase 的是旅程收尾那一张（control-server#323）：不在运输旅程上，不是装货阶段快照。
                    if (phase.ValueKind == JsonValueKind.Null)
                    {
                        return null;
                    }
                    JsonElement deadline = phase.GetProperty("cargoHoldingDeadlineAt");
                    JsonElement reason = phase.GetProperty("closedReason");
                    return new LoadingPhaseSnapshot(
                        phase.GetProperty("state").GetString()!,
                        deadline.ValueKind == JsonValueKind.Null ? null : deadline.GetDateTimeOffset(),
                        reason.ValueKind == JsonValueKind.Null ? null : reason.GetString())
                    {
                        Revision = payload.GetProperty("vehicleBusinessStateRevision").GetInt64()
                    };
                })
                .OfType<LoadingPhaseSnapshot>()
                .OrderBy(snapshot => snapshot.Revision)
        ];
    }

    internal static void AssertStrictlyIncreasing(LoadingPhaseSnapshot[] snapshots)
    {
        long[] revisions = [.. snapshots.Select(snapshot => snapshot.Revision)];
        Assert.Equal(revisions.Distinct().Count(), revisions.Length);
        Assert.Equal(revisions.Order(), revisions);
    }

    private static async Task<ProtocolOutboxRow[]> WaitSnapshotRowsAsync(RuntimeFixture fixture) =>
        [.. (await fixture.Context.ProtocolOutbox.AsNoTracking()
                .Where(row => row.MessageType == "VehicleBusinessStateSnapshot")
                .ToArrayAsync(TestContext.Current.CancellationToken))
            .Where(row => row.PayloadJson.Contains("\"CARGO_HOLDING_WAIT\"", StringComparison.Ordinal))];

    /// <summary>
    /// 把「上一轮派车对这辆车的裁决」写进读口：每一项是一条候选的原因码与它落在哪一侧。一项都不给就是「这一轮问到了这辆车，
    /// 没有任何一侧被占满」——与这辆车不在表里（说不出来）不是一回事。
    /// </summary>
    private static void RecordRound(RuntimeFixture fixture, params (string Reason, string Side)[] verdicts) =>
        fixture.SlotGroupFullness.Record(new DispatchRoundOutcome(
            Round: null!,
            [
                new DispatchVehicleOutcome(
                    fixture.Options.AgvId,
                    fixture.Options.VehicleKey,
                    [
                        .. verdicts.Select(verdict => new DispatchCandidateVerdict(
                            new DispatchCandidateEvaluation(candidate: null!, round: null!, vehicle: null!)
                            {
                                AreaAssignment = new AreaAssignment("N1-1", fixture.Options.DispatchZone, verdict.Side)
                            },
                            verdict.Reason))
                    ])
            ]));

    /// <summary>插入 <c>ProtocolOutbox</c> 且载荷带 <c>CARGO_HOLDING_WAIT</c> 的那一条命令抛一次，如同写到那里时崩了。</summary>
    private sealed class FailOnceOnWaitSnapshotInsert : DbCommandInterceptor
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
                !command.CommandText.Contains("INSERT INTO \"ProtocolOutbox\"", StringComparison.Ordinal))
            {
                return;
            }

            if (command.Parameters.Cast<DbParameter>()
                .Any(parameter => parameter.Value is string value &&
                                  value.Contains("\"CARGO_HOLDING_WAIT\"", StringComparison.Ordinal)))
            {
                Fired = true;
                throw new InvalidOperationException("Injected failure while writing the CARGO_HOLDING_WAIT snapshot.");
            }
        }
    }

    internal static async Task<bool> DepartureCheckSentAsync(RuntimeFixture fixture) =>
        await DepartureChecksSentAsync(fixture) > 0;

    internal static Task<int> DepartureChecksSentAsync(RuntimeFixture fixture) =>
        fixture.Context.ProtocolOutbox.AsNoTracking()
            .CountAsync(row => row.MessageType == "PreDepartureSafetyCheck", TestContext.Current.CancellationToken);
}
