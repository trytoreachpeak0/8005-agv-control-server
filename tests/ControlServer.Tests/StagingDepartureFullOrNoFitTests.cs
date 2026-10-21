using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.StagingPickupNoCountdownTests;

namespace ControlServer.Tests;

/// <summary>
/// 待送点按前后仓判「全满或等着的需求全部装不下」后立即出发（control-server#710，program#184 R1；CP-0012、ADR-cross-0065）——推进段上的样子。
/// </summary>
/// <remarks>
/// <para>
/// 夹具是 control-server#709 那一份（<see cref="StagingPickupNoCountdownTests.ArrangeAsync"/>）：一条需求在本站装上，占前仓（夹具的区表把每个
/// AREA 都分到前仓）。「前仓满」由把前仓其余仓位禁用造出来，账本据此答前仓无空仓。
/// </para>
/// <para>
/// <b>「上一轮派车的裁决」是直接写进读口的</b>，与 <c>Batch7CargoHoldingTests.RecordRound</c> 同一个做法：要证的是推进段怎样读这一份裁决，
/// 不是派车轮怎样得出它。单测夹具没有路网，途中追加的四道门不在链上，9 段、8 条上限那个码在这里造不出真的；真派车那一头由 L2 场景
/// <c>staging-side-full-departs</c>、<c>staging-other-side-fits-holds</c> 取证。每一轮推进之后派车轮会用真裁决替换掉读口，所以每次推进前都重新写一次。
/// </para>
/// <para>
/// <b>机台回归用同一组事实跑三种停靠</b>（<see cref="StagingPickupNoCountdownTests.StopKind"/>）：待送点（规则开）走本票的规则；机台（规则开）与
/// 待送点（规则关）必须与改前一样。
/// </para>
/// </remarks>
public sealed class StagingDepartureFullOrNoFitTests
{
    private const int MachineStationRiotId = 12;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 验收 1、10：前仓满、后仓有空，等着的需求全要前仓。待送点：判满的这一轮就是 <c>VEHICLE_FULL</c>，同一轮发离站核验，不等离站等待。
    /// 机台与规则关：照旧，一侧满不算满，持货等单、不发核验。
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds), MemberType = typeof(StagingPickupNoCountdownTests))]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task OneSideFullAndEveryWaitingDemandNeedingItDepartsOnlyAtTheStagingStation(StopKind kind)
    {
        await using RuntimeFixture fixture = await LoadedAsync(kind);
        await FillFrontAsync(fixture);

        RecordRound(fixture, Waiting(kind, "W-1", "FRONT", DispatchReasonCodes.SlotGroupOccupiedByOwnCargo));
        await TickAsync(fixture);

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal("[\"FRONT\"]", journey.FullSlotPositionsJson);
        if (kind == StopKind.StagingRulesOn)
        {
            Assert.Equal(LoadingPhaseStates.VehicleFull, journey.LoadingPhaseState);
            Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, journey.Stage);
            Assert.Equal(1, await Batch7CargoHoldingTests.DepartureChecksSentAsync(fixture));
            Assert.Equal(
                LoadingPhaseStates.VehicleFull,
                (await Batch7CargoHoldingTests.LoadingPhaseSnapshotsAsync(fixture))[^1].State);
        }
        else
        {
            Assert.Equal(LoadingPhaseStates.CargoHoldingWait, journey.LoadingPhaseState);
            Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, journey.Stage);
            Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), $"{kind}: one full side sent the vehicle on.");
        }
    }

    /// <summary>验收 2：前仓满、后仓有空，等着的需求里有一条要后仓而后仓装得下（合格）：继续等，不发核验。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AWaitingDemandThatStillFitsTheOtherSideHoldsTheVehicle()
    {
        await using RuntimeFixture fixture = await LoadedAsync(StopKind.StagingRulesOn);
        await FillFrontAsync(fixture);

        for (int round = 0; round < 3; round++)
        {
            RecordRound(
                fixture,
                Waiting(StopKind.StagingRulesOn, "W-1", "FRONT", DispatchReasonCodes.SlotGroupOccupiedByOwnCargo),
                Waiting(StopKind.StagingRulesOn, "W-2", "REAR", DispatchAdmissionChain.Eligible));
            await TickAsync(fixture);
        }

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, journey.LoadingPhaseState);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, journey.Stage);
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "A demand that still fits did not hold the vehicle.");
    }

    /// <summary>
    /// 验收 3、10：两侧都满（账本：前仓本车货、其余仓位禁用），这一轮没有任何等着的需求。待送点：立即离站。机台与规则关：照旧 <c>VEHICLE_FULL</c>，
    /// 但还要等完离站等待（五分钟），这一秒不发核验。
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds), MemberType = typeof(StagingPickupNoCountdownTests))]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task BothSidesFullDepartsAtOnceOnlyAtTheStagingStation(StopKind kind)
    {
        await using RuntimeFixture fixture = await LoadedAsync(kind);
        await DisableSlotsAsync(fixture, [.. Enumerable.Range(1, 8).Except(await OwnSlotsAsync(fixture))]);

        RecordRound(fixture);
        await TickAsync(fixture);

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(LoadingPhaseStates.VehicleFull, journey.LoadingPhaseState);
        Assert.Equal("[\"FRONT\",\"REAR\"]", journey.FullSlotPositionsJson);
        if (kind == StopKind.StagingRulesOn)
        {
            Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, journey.Stage);
            Assert.True(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "A vehicle full on both sides kept waiting.");
        }
        else
        {
            Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, journey.Stage);
            Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), $"{kind}: left before its station departure wait.");
        }
    }

    /// <summary>
    /// 验收 4，按 CP-0024 改（cs#800 独立审查 M1）：前仓满、后仓 5、6、7 禁用只剩 1 格，等着的只有一条要后仓 2 篮（派车判
    /// <c>SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE</c>）。后仓可用的仓本来就只有 1 格，它是因禁用仓装不下，不计入「全部装不下」：不离站，持货等单。
    /// 原来（CP-0012）按容量算、立即离站。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task TheOnlyWaitingDemandBlockedByDisabledRearSlotsDoesNotSendItOn()
    {
        await using RuntimeFixture fixture = await LoadedAsync(StopKind.StagingRulesOn);
        await FillFrontAsync(fixture);
        await DisableSlotsAsync(fixture, [5, 6, 7]);

        RecordRound(fixture, Waiting(
            StopKind.StagingRulesOn, "W-1", "REAR", DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable, baskets: 2));
        await TickAsync(fixture);

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, journey.LoadingPhaseState);
        Assert.Equal("[\"FRONT\"]", journey.FullSlotPositionsJson);
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "A demand only disabled slots refuse sent the vehicle on.");
    }

    /// <summary>
    /// cs#800 独立审查 M1 的探针：锚需求装了 1 篮、前仓还空 3 格，后仓 5、6、7 禁用，等着的一条要后仓 2 篮、派车判 <c>SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE</c>。
    /// 只因禁用仓装不下，不计入（CP-0024）：不判满，车不带着 1 篮走。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AWaitingDemandOnlyDisabledSlotsRefuseDoesNotSendAOneBasketVehicleOn()
    {
        await using RuntimeFixture fixture = await LoadedAsync(StopKind.StagingRulesOn);
        await DisableSlotsAsync(fixture, [5, 6, 7]);

        RecordRound(fixture, Waiting(
            StopKind.StagingRulesOn, "W-1", "REAR", DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable, baskets: 2));
        await TickAsync(fixture);

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.NotEqual(LoadingPhaseStates.VehicleFull, journey.LoadingPhaseState);
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "Disabled slots sent a one-basket vehicle on.");
    }

    /// <summary>
    /// cs#800 复审 L3（审查人的探针，变异 mI 下存活后补的守护）：没有重连，同一侧既有已装货（已装的仓在会话基线里）又有禁用仓。等着的一条要这一侧
    /// 「已装篮数 + 2」篮，派车判 <c>SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE</c>：这一侧能用的仓只有已装那几格加 1 格空仓，比它要的少，挡住它的只是禁用仓，
    /// 不计入，车不带着一批就走。能用的仓若写成「基线个数加本侧已装个数」，已装的会被算两遍，这条就红。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task SameSideLoadedAndDisabledWithoutReconnectDoesNotSendItOn()
    {
        await using RuntimeFixture fixture = await LoadedAsync(StopKind.StagingRulesOn);
        int[] own = await OwnSlotsAsync(fixture);
        Assert.All(own, slot => Assert.InRange(slot, 1, 4));
        await DisableSlotsAsync(fixture, [.. Enumerable.Range(1, 4).Except(own).Skip(1)]);
        RecordRound(fixture, Waiting(
            StopKind.StagingRulesOn, "W-1", "FRONT", DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable, baskets: own.Length + 2));
        await TickAsync(fixture);
        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.NotEqual(LoadingPhaseStates.VehicleFull, journey.LoadingPhaseState);
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "Disabled slots on the loaded side sent the vehicle on.");
    }

    /// <summary>
    /// cs#800 独立审查 M1 的另一半：装货途中重连，新会话的基线里没有已装的那一格（会话建立时它有货）。前仓可用的仓只剩基线里 1 格，等着的一条要前仓 2 篮，
    /// 派车同样判 <c>SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE</c>——但这一侧算上已装的那一格有 2 格，挡住它的是已装货物，算容量：判满，离站。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AfterAReconnectMidLoadACandidateTheLoadedSlotBlocksStillCounts()
    {
        await using RuntimeFixture fixture = await LoadedAsync(StopKind.StagingRulesOn);
        int[] own = await OwnSlotsAsync(fixture);
        await SetSlotsOccupiedInTheSessionBaselineAsync(fixture, own);
        await DisableSlotsAsync(fixture, [.. Enumerable.Range(1, 4).Except(own).Skip(1)]);

        RecordRound(fixture, Waiting(
            StopKind.StagingRulesOn, "W-1", "FRONT", DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable, baskets: 2));
        await TickAsync(fixture);

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(LoadingPhaseStates.VehicleFull, journey.LoadingPhaseState);
        Assert.True(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "Loaded cargo blocking the only candidate did not send the vehicle on.");
    }

    /// <summary>
    /// cs#800 独立审查 L1：为离站请求移动的那一轮是保持点。判满后同站追加、核验已发往同站第 2 个停靠；这时前仓放开、等着的那条变得装得下，答复核验的那一轮
    /// （服务端为开往第 2 停靠请求移动）不撤回 <c>VEHICLE_FULL</c>。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task TheRoundThatRequestsTheMoveKeepsTheJudgement()
    {
        const string correctionId = "70000000-0000-4000-8000-000000000801";
        await using RuntimeFixture fixture = await LoadedAsync(StopKind.StagingRulesOn);
        await FillFrontAsync(fixture);
        await OpenCorrectionAsync(fixture, correctionId);
        RecordFrontOnlyWaiting(fixture);
        await TickAsync(fixture);
        fixture.Catalog.Set(StagingDemand(fixture), StagingDemand(fixture, SecondDemand, SecondSublot, Now.AddMinutes(-9)));
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        await CloseCorrectionAsync(fixture, correctionId);
        RecordFrontOnlyWaiting(fixture);
        await TickAsync(fixture);
        Assert.Equal([StagingStationName], await DepartureTargetsAsync(fixture));
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await fixture.RuntimeAsync()).Stage);

        await SetSlotAvailabilityAsync(fixture, [1, 2, 3, 4], "ENABLED");
        RecordRound(fixture, Waiting(StopKind.StagingRulesOn, "W-1", "FRONT", DispatchAdmissionChain.Eligible));
        await Batch7StopDrivenAdvanceDriver.AnswerDepartureSafetyAsync(fixture, Demand, Batch7StopDrivenAdvanceDriver.FirstSafetyResultId);

        JourneyRuntimeRow moving = await fixture.RuntimeAsync();
        Assert.NotEqual(JourneyRuntimeStage.AwaitingDepartureSafety, moving.Stage);
        Assert.Equal(LoadingPhaseStates.VehicleFull, moving.LoadingPhaseState);
    }

    /// <summary>
    /// 验收 5：等着的需求全部因非容量原因被拒，或者根本没有等着的需求，或者装不下的那条在别的站取货：不离站（S 为空，等客户出发）。
    /// 电量拒绝排在站点解析之后，候选带着本站的路线——它不离站，只能是因为原因码不算容量。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    [InlineData("nothing waiting")]
    [InlineData("mandatory charge")]
    [InlineData("task type not admitted")]
    [InlineData("not in zone")]
    [InlineData("own cargo, but picked up at another station")]
    [InlineData("own cargo, but not a staging demand")]
    [InlineData("larger than the side")]
    public async Task NothingButNonCapacityRefusalsHoldsTheVehicle(string row)
    {
        await using RuntimeFixture fixture = await LoadedAsync(StopKind.StagingRulesOn);
        await FillFrontAsync(fixture);

        DispatchCandidateVerdict[] verdicts = row switch
        {
            "nothing waiting" => [],
            "mandatory charge" => [Waiting(StopKind.StagingRulesOn, "W-1", "REAR", DispatchReasonCodes.MandatoryChargeRequired)],
            "task type not admitted" =>
                [Verdict("W-1", TransportTaskTypes.StagingToWire, null, null, VehicleTaskTypeAdmissionCriterion.TaskTypeNotAdmittedReason)],
            "not in zone" =>
                [Waiting(StopKind.StagingRulesOn, "W-1", "FRONT", DispatchZoneVehicleCriterion.VehicleNotInZoneReason)],
            "own cargo, but picked up at another station" =>
                [Verdict("W-1", TransportTaskTypes.StagingToWire, 306, "FRONT", DispatchReasonCodes.SlotGroupOccupiedByOwnCargo)],
            "own cargo, but not a staging demand" =>
                [Verdict("W-1", TransportTaskTypes.WireToGate, StagingStationRiotId, "FRONT", DispatchReasonCodes.SlotGroupOccupiedByOwnCargo)],
            "larger than the side" =>
                [Waiting(StopKind.StagingRulesOn, "W-1", "REAR", DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup)],
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, null),
        };
        for (int round = 0; round < 3; round++)
        {
            RecordRound(fixture, verdicts);
            await TickAsync(fixture);
        }

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.True(
            journey.LoadingPhaseState == LoadingPhaseStates.CargoHoldingWait,
            $"{row}: {journey.LoadingPhaseState}");
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), $"{row}: the vehicle left by itself.");
    }

    /// <summary>
    /// 验收 6：等着的需求一部分因容量（前仓本车货）、一部分因准入、电量被拒，没有一条装得下：立即离站。被准入、电量拒的不算「装得下」，也不阻止离站。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task CapacityAndAdmissionRefusalsTogetherDepart()
    {
        await using RuntimeFixture fixture = await LoadedAsync(StopKind.StagingRulesOn);
        await FillFrontAsync(fixture);

        RecordRound(
            fixture,
            Verdict("W-0", TransportTaskTypes.StagingToWire, null, null, VehicleTaskTypeAdmissionCriterion.TaskTypeNotAdmittedReason),
            Waiting(StopKind.StagingRulesOn, "W-1", "FRONT", DispatchReasonCodes.SlotGroupOccupiedByOwnCargo),
            Waiting(StopKind.StagingRulesOn, "W-2", "REAR", DispatchReasonCodes.MandatoryChargeRequired));
        await TickAsync(fixture);

        Assert.Equal(LoadingPhaseStates.VehicleFull, (await fixture.RuntimeAsync()).LoadingPhaseState);
        Assert.True(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "Admission refusals held a vehicle that could fit nothing.");
    }

    /// <summary>
    /// 验收 7：等着的需求只因途中追加的 9 段或 8 条上限被拒（两者同一个码）：立即离站。被 Order 98 拒的候选走不到仓位判据，没有落在哪一侧，
    /// 前仓也不必满——它装不进这趟旅程，与哪一侧有空无关。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task OnlyThePlanLimitRefusingTheWaitingDemandsDeparts()
    {
        await using RuntimeFixture fixture = await LoadedAsync(StopKind.StagingRulesOn);

        RecordRound(
            fixture,
            Waiting(StopKind.StagingRulesOn, "W-1", side: null, DispatchReasonCodes.EnRouteAppendPlanLimitReached),
            Waiting(StopKind.StagingRulesOn, "W-2", side: null, DispatchReasonCodes.EnRouteAppendPlanLimitReached));
        await TickAsync(fixture);

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(LoadingPhaseStates.VehicleFull, journey.LoadingPhaseState);
        Assert.DoesNotContain("REAR", journey.FullSlotPositionsJson, StringComparison.Ordinal);
        Assert.True(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "The plan limit held the vehicle.");
    }

    /// <summary>
    /// 验收 8：重启之后、派车轮第一轮之前，读口是空的：前仓满、等着的需求本该让车走，但这一轮说不出来，不翻状态、不自动走、不多发快照。
    /// 第一轮派车给出结论，那一轮才走。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task BeforeTheFirstRoundAfterARestartNothingChangesAndNothingLeaves()
    {
        await using RuntimeFixture fixture = await LoadedAsync(StopKind.StagingRulesOn);
        await FillFrontAsync(fixture);
        int snapshotsBefore = (await Batch7CargoHoldingTests.LoadingPhaseSnapshotsAsync(fixture)).Length;

        fixture.SlotGroupFullness = new();
        await fixture.RecreateEngineAsync();
        await TickAsync(fixture);

        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, (await fixture.RuntimeAsync()).LoadingPhaseState);
        Assert.Equal(snapshotsBefore, (await Batch7CargoHoldingTests.LoadingPhaseSnapshotsAsync(fixture)).Length);
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "The vehicle left on a judgement nobody made.");

        RecordRound(fixture, Waiting(StopKind.StagingRulesOn, "W-1", "FRONT", DispatchReasonCodes.SlotGroupOccupiedByOwnCargo));
        await TickAsync(fixture);
        Assert.True(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "The first round's judgement sent nobody on.");
    }

    /// <summary>
    /// 验收 9：判满的这一轮已经开着纠错。装货阶段照样判成 <c>VEHICLE_FULL</c>，但纠错挡车，不发核验。纠错期间等着的需求变了（后仓来了一条装得下的）：
    /// 核验还没发出，判定撤回（CP-0018 取代原来的「锁住不翻回」，control-server#800）。那一条又不在了、纠错闭环之后的那一轮重新判满，立即走，不再给宽限。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AnOpenCorrectionHoldsAFullVehicleThatLeavesOnceItCloses()
    {
        await using RuntimeFixture fixture = await LoadedAsync(StopKind.StagingRulesOn);
        await FillFrontAsync(fixture);
        JourneyDemandRow membership = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == Demand, Token);
        fixture.Context.RecoveryWorkflows.Add(new RecoveryWorkflowRow
        {
            WorkflowId = "70000000-0000-4000-8000-000000000710",
            WorkflowType = "LOAD_CORRECTION",
            AgvId = fixture.Options.AgvId,
            DemandId = Demand,
            SlotOperationAttemptId = membership.LoadSlotOperationAttemptId,
            SlotsJson = membership.TargetSlotsJson,
            State = RecoveryWorkflowState.CommandPending,
            RequestMessageId = "70000000-0000-4000-8000-000000000711",
            RequestContentHash = new string('f', 64),
            CreatedAt = fixture.Clock.GetUtcNow(),
            UpdatedAt = fixture.Clock.GetUtcNow()
        });
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        RecordRound(fixture, Waiting(StopKind.StagingRulesOn, "W-1", "FRONT", DispatchReasonCodes.SlotGroupOccupiedByOwnCargo));
        await TickAsync(fixture);
        JourneyRuntimeRow held = await fixture.RuntimeAsync();
        Assert.Equal(LoadingPhaseStates.VehicleFull, held.LoadingPhaseState);
        Assert.Equal("LOAD_CORRECTION_IN_PROGRESS", held.BlockReasonCode);
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "An open correction did not hold the vehicle.");

        RecordRound(
            fixture,
            Waiting(StopKind.StagingRulesOn, "W-1", "FRONT", DispatchReasonCodes.SlotGroupOccupiedByOwnCargo),
            Waiting(StopKind.StagingRulesOn, "W-2", "REAR", DispatchAdmissionChain.Eligible));
        await TickAsync(fixture);
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, (await fixture.RuntimeAsync()).LoadingPhaseState);
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture));

        RecoveryWorkflowRow correction = await fixture.Context.RecoveryWorkflows
            .SingleAsync(row => row.WorkflowId == "70000000-0000-4000-8000-000000000710", Token);
        correction.State = RecoveryWorkflowState.Reconciled;
        correction.UpdatedAt = fixture.Clock.GetUtcNow();
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        RecordFrontOnlyWaiting(fixture);
        await TickAsync(fixture);
        JourneyRuntimeRow left = await fixture.RuntimeAsync();
        Assert.Null(left.BlockReasonCode);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, left.Stage);
        Assert.True(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "The closed correction gave the vehicle another wait.");
    }

    /// <summary>验收 11：一条前仓 sublot 的装货命令只开前仓，后仓的只开后仓（现有行为，端到端补一条）。</summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    [InlineData("FRONT", 1, 4)]
    [InlineData("REAR", 5, 8)]
    public async Task AStagingSublotOpensOnlyTheSideItsAreaIsAssigned(string side, int firstSlot, int lastSlot)
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await fixture.ImportAreaAssignmentsAsync(
            [.. RuntimeFixture.DefaultAssignedAreas.Select(area => new AreaAssignment(area, fixture.Options.DispatchZone, side))]);
        await AllowEnRouteAppendAsync(fixture);
        await fixture.AdvanceToLoadResultAsync();

        ProtocolOutboxRow command = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageType == "SlotOperationCommand", Token);
        using JsonDocument document = JsonDocument.Parse(command.PayloadJson);
        int[] opened = [.. document.RootElement.GetProperty("payload").GetProperty("slots").EnumerateArray()
            .Select(slot => slot.GetInt32())];
        Assert.NotEmpty(opened);
        Assert.All(opened, slot => Assert.InRange(slot, firstSlot, lastSlot));
    }

    /// <summary>
    /// 审查第二阶段第 2 项：两侧都有一条候选只因本车货占侧而装不下（两侧都记成满），但还有一条小需求合格、装得下：不走。两侧都还有空仓，
    /// 「每一侧都满」按空仓判，不按记满的侧数判。被占侧的两条篮数都比那一侧只扣已装货物后的空仓多（前仓 3、后仓 2），是被已装货物挡住而不只是预留
    /// （control-server#800）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task OwnCargoOnBothSidesWithASmallDemandThatStillFitsHoldsTheVehicle()
    {
        await using RuntimeFixture fixture = await LoadedAsync(StopKind.StagingRulesOn);
        await DisableSlotsAsync(fixture, [7, 8]);

        for (int round = 0; round < 3; round++)
        {
            RecordRound(
                fixture,
                Waiting(StopKind.StagingRulesOn, "W-1", "FRONT", DispatchReasonCodes.SlotGroupOccupiedByOwnCargo, baskets: 4),
                Waiting(StopKind.StagingRulesOn, "W-2", "REAR", DispatchReasonCodes.SlotGroupOccupiedByOwnCargo, baskets: 3),
                Waiting(StopKind.StagingRulesOn, "W-3", "REAR", DispatchAdmissionChain.Eligible));
            await TickAsync(fixture);
        }

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal("[\"FRONT\",\"REAR\"]", journey.FullSlotPositionsJson);
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, journey.LoadingPhaseState);
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "A demand that still fits did not hold the vehicle.");
    }

    /// <summary>
    /// 审查 L5：车还在开往待送点的路上（<c>AwaitingPickupArrival</c>），前仓按预留已满、等着的需求全要前仓：不判，不锁 <c>VEHICLE_FULL</c>。
    /// 到站之后的那一轮才判。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task NothingIsJudgedBeforeTheVehicleReachesTheStagingStation()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        await FillFrontAsync(fixture);

        for (int round = 0; round < 2; round++)
        {
            RecordRound(fixture, Waiting(StopKind.StagingRulesOn, "W-1", "FRONT", DispatchReasonCodes.SlotGroupOccupiedByOwnCargo));
            await TickAsync(fixture);
        }
        JourneyRuntimeRow driving = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, driving.Stage);
        Assert.NotEqual(LoadingPhaseStates.VehicleFull, driving.LoadingPhaseState);

        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", driving.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = driving.PickupStationRiotId };
        RecordRound(fixture, Waiting(StopKind.StagingRulesOn, "W-1", "FRONT", DispatchReasonCodes.SlotGroupOccupiedByOwnCargo));
        await TickAsync(fixture);
        RecordRound(fixture, Waiting(StopKind.StagingRulesOn, "W-1", "FRONT", DispatchReasonCodes.SlotGroupOccupiedByOwnCargo));
        await TickAsync(fixture);
        JourneyRuntimeRow arrived = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, arrived.Stage);
        Assert.Equal(LoadingPhaseStates.VehicleFull, arrived.LoadingPhaseState);
    }

    /// <summary>
    /// 审查 M1（第二阶段第 3 项）：一趟同时有待送点与机台取货的旅程。待送点上等了四分钟（期限之内；CP-0014 起那里装上一篮就有持货期限，
    /// control-server#727），因 9 段上限判走、一侧还空着；到了后面的机台取货停靠，按机台规则持货等单，持货期限从机台那一批装货闭环算起，到期照常关闭、离站。
    /// </summary>
    /// <remarks>
    /// 两个变异各红一处：持货期限的豁免改回按整趟旅程判，机台停靠永远不到期，最后一段红；离开待送点不清空起算点，期限从待送点第一批装货算起、
    /// 早已过了，车还没到机台，机台那条需求就被持货截断放回，到站那一段红。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AMachinePickupAfterTheStagingStationKeepsItsOwnHoldingDeadline()
    {
        const string machineDemand = "10000000-0000-4000-8000-000000000710";
        const string machineSublot = "SUBLOT-710";
        await using RuntimeFixture fixture = await LoadedAsync(StopKind.StagingRulesOn);
        JsonElement atStagingRow = Assert.Single(await CargoHoldingRowsAsync(fixture));
        Assert.True(atStagingRow.GetProperty("atStaging").GetBoolean());
        Assert.Equal(
            (await fixture.RuntimeAsync()).CargoHoldingStartedAt + fixture.Options.CargoHoldingTimeout,
            atStagingRow.GetProperty("cargoHoldingDeadlineAt").GetDateTimeOffset());

        for (int step = 0; step < 2; step++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(2));
            await TickAsync(fixture);
        }
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, (await fixture.RuntimeAsync()).LoadingPhaseState);

        fixture.BoxCounts.Set(machineSublot, 8);
        // 追加进来的需求也要在 MES 目录里：录入之后服务端按目录重校验，目录里没有它，录入不会落成装货命令。
        fixture.Catalog.Set(StagingDemand(fixture), fixture.Demand(machineDemand, machineSublot, Now.AddMinutes(-9), area: "N1-2"));
        await Batch7ThreeStopJourneyTests.AppendDemandAsync(fixture, machineDemand, machineSublot, "N1-2", 13);
        RecordRound(fixture, Waiting(StopKind.StagingRulesOn, "W-1", side: null, DispatchReasonCodes.EnRouteAppendPlanLimitReached));
        await TickAsync(fixture);
        JourneyRuntimeRow leaving = await fixture.RuntimeAsync();
        Assert.Equal(LoadingPhaseStates.VehicleFull, leaving.LoadingPhaseState);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, leaving.Stage);
        Assert.Equal(["N1-2"], await DepartureTargetsAsync(fixture));

        await Batch7StopDrivenAdvanceDriver.AnswerDepartureSafetyAsync(fixture, Demand, Batch7StopDrivenAdvanceDriver.FirstSafetyResultId);
        Assert.Null((await fixture.RuntimeAsync()).CargoHoldingStartedAt);
        await Batch7StopDrivenAdvanceDriver.ArriveAtCurrentStopAsync(fixture, Demand, "TO_GATE");
        await Batch7StopDrivenAdvanceDriver.EnterSublotAsync(
            fixture, Demand, machineSublot, Batch7StopDrivenAdvanceDriver.SecondSubmissionId);
        await Batch7StopDrivenAdvanceDriver.SettleLoadAsync(fixture, machineDemand);

        JourneyRuntimeRow holding = await fixture.RuntimeAsync();
        Assert.Equal(JourneyDemandStatuses.Loaded, (await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == machineDemand, Token)).Status);
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, holding.LoadingPhaseState);
        Assert.NotNull(holding.CargoHoldingStartedAt);
        Assert.Single(await DepartureTargetsAsync(fixture));
        JsonElement atMachineRow = Assert.Single(await CargoHoldingRowsAsync(fixture));
        Assert.False(atMachineRow.GetProperty("atStaging").GetBoolean());
        Assert.Equal(
            holding.CargoHoldingStartedAt + fixture.Options.CargoHoldingTimeout,
            atMachineRow.GetProperty("cargoHoldingDeadlineAt").GetDateTimeOffset());

        fixture.Clock.Advance(fixture.Options.CargoHoldingTimeout);
        await TickAsync(fixture);
        JourneyRuntimeRow left = await fixture.RuntimeAsync();
        Assert.Equal(LoadingPhaseStates.Closed, left.LoadingPhaseState);
        Assert.Equal(LoadingClosedReasons.CargoHoldingTimeout, left.LoadingClosedReason);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, left.Stage);
        Assert.Equal(2, (await DepartureTargetsAsync(fixture)).Length);
    }

    /// <summary>
    /// 审查 M2（第二阶段第 1 项）：前仓满、纠错开着时判 <c>VEHICLE_FULL</c>；判走之后又追加进一条后仓装得下的待送需求（与派车轮同一条写入路径，
    /// <c>WireToGateStore.AppendToJourneyAsync</c>）；纠错关闭。离站核验先发往同站新开的停靠，车原地走过去装上它，装上之后立即开往卸货站。
    /// 全程不抛异常、不进 <c>Blocked</c>。
    /// </summary>
    /// <remarks>
    /// 单测夹具没有路网，派车轮的在途链上没有途中追加那道门，合格的候选在这里追加不进来，所以追加直接调写入路径；读口照旧手写。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task ADemandAppendedAfterTheVehicleWasJudgedFullIsLoadedInPlaceAndThenTheVehicleLeaves()
    {
        await using RuntimeFixture fixture = await LoadedAsync(StopKind.StagingRulesOn);
        await FillFrontAsync(fixture);
        const string correctionId = "70000000-0000-4000-8000-000000000720";
        await OpenCorrectionAsync(fixture, correctionId);

        RecordRound(fixture, Waiting(StopKind.StagingRulesOn, "W-1", "FRONT", DispatchReasonCodes.SlotGroupOccupiedByOwnCargo));
        await TickAsync(fixture);
        Assert.Equal(LoadingPhaseStates.VehicleFull, (await fixture.RuntimeAsync()).LoadingPhaseState);
        Assert.Empty(await DepartureTargetsAsync(fixture));

        fixture.Catalog.Set(StagingDemand(fixture), StagingDemand(fixture, SecondDemand, SecondSublot, Now.AddMinutes(-9)));
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        // 那个辅助方法为它预留了两个仓（5、6），箱数却给 2（每篮 4 箱，只算一篮）：录入时按箱数重算出 1 篮，与预留不符被拒。这里按两篮给。
        fixture.BoxCounts.Set(SecondSublot, 8);
        await CloseCorrectionAsync(fixture, correctionId);
        RecordRound(fixture, Waiting(StopKind.StagingRulesOn, "W-1", "FRONT", DispatchReasonCodes.SlotGroupOccupiedByOwnCargo));
        await TickAsync(fixture);
        AssertNeverBlocked(await fixture.RuntimeAsync());
        Assert.Equal([StagingStationName], await DepartureTargetsAsync(fixture));

        // 判满每轮重判（control-server#800）：派车轮每一轮之后会用真裁决换掉读口，每一轮之前都写回「那条前仓的还在等、被已装货物挡住」。
        RecordFrontOnlyWaiting(fixture);
        await Batch7StopDrivenAdvanceDriver.AnswerDepartureSafetyAsync(fixture, Demand, Batch7StopDrivenAdvanceDriver.FirstSafetyResultId);
        AssertNeverBlocked(await fixture.RuntimeAsync());
        RecordFrontOnlyWaiting(fixture);
        await Batch7StopDrivenAdvanceDriver.ArriveAtCurrentStopAsync(fixture, Demand, "TO_GATE");
        AssertNeverBlocked(await fixture.RuntimeAsync());
        RecordFrontOnlyWaiting(fixture);
        await Batch7StopDrivenAdvanceDriver.EnterSublotAsync(
            fixture, Demand, SecondSublot, Batch7StopDrivenAdvanceDriver.SecondSubmissionId);
        AssertNeverBlocked(await fixture.RuntimeAsync());
        RecordFrontOnlyWaiting(fixture);
        await Batch7StopDrivenAdvanceDriver.SettleLoadAsync(fixture, SecondDemand);

        JourneyRuntimeRow left = await fixture.RuntimeAsync();
        AssertNeverBlocked(left);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == SecondDemand, Token)).Status);
        Assert.Equal(LoadingPhaseStates.VehicleFull, left.LoadingPhaseState);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, left.Stage);
        string[] targets = await DepartureTargetsAsync(fixture);
        Assert.Equal(2, targets.Length);
        Assert.NotEqual(StagingStationName, targets[1]);
    }

    /// <summary>
    /// 审查 L3：持货看板上，待送点的 <c>VEHICLE_FULL</c> 与在待送点装过货的旅程以 <c>VEHICLE_FULL</c> 结束，都写「已满或装不下」；机台照旧。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [InlineData("VEHICLE_FULL", null, true, false, "<td>已满或装不下</td>")]
    [InlineData("VEHICLE_FULL", null, false, false, "<td>已装满</td>")]
    [InlineData("CLOSED", "VEHICLE_FULL", false, true, "<td>已满或装不下</td>")]
    [InlineData("CLOSED", "VEHICLE_FULL", false, false, "<td>两侧装满</td>")]
    public void TheCargoHoldingCardSaysFullOrNothingFitsForTheStagingStation(
        string state, string? closedReason, bool atStaging, bool loadedAtStaging, string expected)
    {
        using JsonDocument fact = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            journeys = new[]
            {
                new
                {
                    agvId = "agv02", stationId = StagingStationName, loadingPhaseState = state, closedReason,
                    frontFull = true, rearFull = false, atStaging, loadedAtStaging,
                },
            },
        }));

        string html = new ControlServer.Dashboard.CargoHoldingCard().RenderFact(fact.RootElement);

        Assert.Contains(expected, html, StringComparison.Ordinal);
    }

    /// <summary>
    /// control-server#780（v5 生产 10-19 06:17 那一次的形状）：装上一篮后判为 <c>VEHICLE_FULL</c>，同站追加的需求新开了第 2 个停靠，车「到」第 2 停靠后
    /// 客户点出发。照样 <c>ACCEPTED</c>、放回没装的那条、装货阶段不改判；这一轮不抛、不整轮失败，发给车的每一行都在发件箱里。
    /// </summary>
    /// <remarks>
    /// 修之前这一轮抛 <c>KeyNotFoundException</c>：出发那一支按第 2 停靠与当前基准重算一个装货阶段 id 去发，而 <c>VEHICLE_FULL</c> 时什么都没暂存，
    /// 第 2 停靠上按那个号落库的是到站那一张（停靠自己的 id），不是装货阶段那一张。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task DepartureAtAnAppendedStopAfterTheVehicleWasJudgedFullSendsOnlyRowsOnFile()
    {
        await using RuntimeFixture fixture = await FullThenAtTheAppendedStopAsync();
        int businessBefore = await BusinessStatesOnFileAsync(fixture);

        string departureRequestId = await StagingDepartureRequestTests.RequestDepartureAsync(fixture);
        await StagingDepartureRequestTests.RunAsync(fixture);

        await AssertFullDepartureAcceptedAsync(fixture, departureRequestId, businessBefore);
    }

    /// <summary>
    /// control-server#780（v5 生产 10-20 01:26 那一次的形状）：车到待送点时一篮未装就判为 <c>VEHICLE_FULL</c>，之后在本站装上锚需求、同站追加的需求新开第 2 个停靠，
    /// 到第 2 停靠后客户点出发。与装满后判出的那一种同一个结论。CP-0018 之后 0 篮不会再因预留判满；这里等着的候选只因途中追加的 9 段上限被拒（容量，与仓位无关），
    /// 这个形状照样到得了（cs#800 独立审查 L5）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task DepartureAfterTheVehicleWasJudgedFullWithNothingLoadedSendsOnlyRowsOnFile()
    {
        await using RuntimeFixture fixture = await FullThenAtTheAppendedStopAsync(loadedBeforeFull: false);
        int businessBefore = await BusinessStatesOnFileAsync(fixture);

        string departureRequestId = await StagingDepartureRequestTests.RequestDepartureAsync(fixture);
        RecordPlanLimitWaiting(fixture);
        await StagingDepartureRequestTests.RunAsync(fixture);

        await AssertFullDepartureAcceptedAsync(fixture, departureRequestId, businessBefore);
    }

    /// <summary>
    /// control-server#780 回归：没判满时受理出发，装货阶段以 <c>OPERATOR_DEPARTED</c> 关闭的那张业务状态快照照旧暂存、在同一轮发出，
    /// 发出去的就是暂存的那一行（id 由停靠与它自己的修订号派生）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task DepartureWhenNotFullSendsTheLoadingPhaseSnapshotItStaged()
    {
        await using RuntimeFixture fixture = await LoadedAsync(StopKind.StagingRulesOn);
        fixture.Catalog.Set(StagingDemand(fixture), StagingDemand(fixture, SecondDemand, SecondSublot, Now.AddMinutes(-9)));
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        JourneyStopRow standing = await Batch7StopDrivenAdvanceDriver.CurrentStopAsync(fixture, Demand);
        int linesBefore = fixture.Peer.Lines.Count;

        string departureRequestId = await StagingDepartureRequestTests.RequestDepartureAsync(fixture);
        await StagingDepartureRequestTests.RunAsync(fixture);

        Assert.Equal(
            "ACCEPTED",
            (await StagingDepartureRequestTests.ResultAsync(fixture, departureRequestId)).GetProperty("outcome").GetString());
        ProtocolOutboxRow closed = Assert.Single(
            await StagedLoadingPhaseRowsAsync(fixture),
            row => ClosedReasonOf(row.PayloadJson) == LoadingClosedReasons.OperatorDeparted);
        Assert.Equal(
            JourneyPlanBuilder.StableGuid($"{standing.StopId}|{RevisionOf(closed.PayloadJson)}", "loading-phase"),
            closed.MessageId);
        Assert.Contains(closed.MessageId, SentMessageIds(fixture, linesBefore));
        await AssertEverySentLineIsOnFileAsync(fixture);
    }

    /// <summary>
    /// control-server#780 重放：判满后在追加停靠上受理了出发，之后服务端重启。重启后的第一轮不抛，不去发一行不存在的报文；车照常离站。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AfterARestartTheFirstRoundSendsNothingThatIsNotOnFile()
    {
        await using RuntimeFixture fixture = await FullThenAtTheAppendedStopAsync();
        string departureRequestId = await StagingDepartureRequestTests.RequestDepartureAsync(fixture);
        await StagingDepartureRequestTests.RunAsync(fixture);
        Assert.Equal(
            "ACCEPTED",
            (await StagingDepartureRequestTests.ResultAsync(fixture, departureRequestId)).GetProperty("outcome").GetString());

        fixture.SlotGroupFullness = new SlotGroupFullnessBoard();
        await fixture.RecreateEngineAsync();
        await StagingDepartureRequestTests.RunAsync(fixture);
        await StagingDepartureRequestTests.RunAsync(fixture);

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        AssertNeverBlocked(journey);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, journey.Stage);
        await AssertEverySentLineIsOnFileAsync(fixture);
    }

    /// <summary>
    /// control-server#780：判满后在追加停靠上，车的会话因本服务端自己的在途单而未就绪（<see cref="PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync"/>）
    /// 时点了出发。不论这一轮答成什么，都不抛、不去发一行不存在的报文；会话回到就绪之后的一轮同样如此。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task DepartureWhileTheSessionIsNotReadyOnTheOwnOrderSendsOnlyRowsOnFile()
    {
        await using RuntimeFixture fixture = await FullThenAtTheAppendedStopAsync();
        await PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync(fixture);
        fixture.Context.ChangeTracker.Clear();

        string departureRequestId = await StagingDepartureRequestTests.RequestDepartureAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await AssertEverySentLineIsOnFileAsync(fixture);

        await fixture.RestoreSessionReadyAsync();
        await StagingDepartureRequestTests.RunAsync(fixture);
        await StagingDepartureRequestTests.RunAsync(fixture);

        Assert.Equal(
            "ACCEPTED",
            (await StagingDepartureRequestTests.ResultAsync(fixture, departureRequestId)).GetProperty("outcome").GetString());
        AssertNeverBlocked(await fixture.RuntimeAsync());
        await AssertEverySentLineIsOnFileAsync(fixture);
    }

    /// <summary>
    /// control-server#800（CP-0018 修订项一第一种情形；10-20 agv02 在 217 的形状）：车到待送点，一篮没装，两侧都被本车预留占满（前仓只剩锚需求那一格、
    /// 后仓只剩追加的那条要的两格）。判满只按已装货物，预留不算：不判满，车持货等单，不发离站核验。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task NothingLoadedWithBothSidesTakenByReservationsIsNotFull()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await DisableSlotsAsync(fixture, [2, 3, 4, 7, 8]);
        JourneyRuntimeRow driving = await fixture.RuntimeAsync();
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", driving.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = driving.PickupStationRiotId };
        for (int round = 0; round < 2; round++)
        {
            RecordRound(fixture);
            await TickAsync(fixture);
        }
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
        fixture.Catalog.Set(StagingDemand(fixture), StagingDemand(fixture, SecondDemand, SecondSublot, Now.AddMinutes(-9)));
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        Assert.Equal((0, 0), await DispatchFreeSlotsAsync(fixture));

        for (int round = 0; round < 2; round++)
        {
            RecordRound(fixture);
            await TickAsync(fixture);
        }

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.NotEqual(LoadingPhaseStates.VehicleFull, journey.LoadingPhaseState);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, journey.Stage);
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "Reservations alone sent an empty vehicle on.");
    }

    /// <summary>
    /// control-server#800（CP-0018 修订项一第一种情形与调度 10-21 对「物理仓位」的解释）：前仓三格禁用、后仓全部禁用，剩下那一格装着锚需求的货。
    /// 可用的仓全被已装货物占满：判满，立即离站。这时派车按已装或已预留核对，两侧也都没有空仓，不会再往这一趟追加（修订项一末句）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task EveryUsableSlotTakenByLoadedCargoIsFullAndLeavesDispatchNoRoom()
    {
        await using RuntimeFixture fixture = await LoadedAsync(StopKind.StagingRulesOn);
        await DisableSlotsAsync(fixture, [.. Enumerable.Range(1, 8).Except(await OwnSlotsAsync(fixture))]);

        RecordRound(fixture);
        await TickAsync(fixture);

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(LoadingPhaseStates.VehicleFull, journey.LoadingPhaseState);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, journey.Stage);
        Assert.Equal((0, 0), await DispatchFreeSlotsAsync(fixture));
    }

    /// <summary>
    /// control-server#800（CP-0018 第五节第二张表第 1 项，用户 10-21 定「不算」）：前仓被已装货物占满；后仓三格可用、两格被追加那条预留。
    /// 等着的那条要后仓两篮，派车因本车占位拒它（<c>SLOT_GROUP_OCCUPIED_BY_OWN_CARGO</c>），但只算已装货物后仓还有三格：它只是被预留挡住，不计入
    /// 「全部装不下」。没有别的候选：不判满，车持货等单。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AWaitingDemandRefusedOnlyForTheVehiclesOwnReservationsDoesNotSendItOn()
    {
        await using RuntimeFixture fixture = await LoadedAsync(StopKind.StagingRulesOn);
        await FillFrontAsync(fixture);
        await DisableSlotsAsync(fixture, [8]);
        fixture.Catalog.Set(StagingDemand(fixture), StagingDemand(fixture, SecondDemand, SecondSublot, Now.AddMinutes(-9)));
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        Assert.Equal((0, 1), await DispatchFreeSlotsAsync(fixture));

        RecordRound(fixture, Waiting(StopKind.StagingRulesOn, "W-1", "REAR", DispatchReasonCodes.SlotGroupOccupiedByOwnCargo, baskets: 2));
        await TickAsync(fixture);

        Assert.NotEqual(LoadingPhaseStates.VehicleFull, (await fixture.RuntimeAsync()).LoadingPhaseState);
    }

    /// <summary>
    /// control-server#800（CP-0018 第五节第二张表第 2 项，用户 10-21 定「到离站安全核验发出为止」）：判满之后、离站核验发出之前（一个开着的纠错把车扣在本站），
    /// 前仓的另外三格放开、等着的那条变得装得下：撤回判定，回到持货等单，车上收到一张 <c>CARGO_HOLDING_WAIT</c>。纠错关了也不走。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task BeforeTheDepartureCheckIsSentAFullJudgementIsWithdrawnOnceSomethingFits()
    {
        const string correctionId = "70000000-0000-4000-8000-000000000800";
        await using RuntimeFixture fixture = await LoadedAsync(StopKind.StagingRulesOn);
        await FillFrontAsync(fixture);
        await OpenCorrectionAsync(fixture, correctionId);
        RecordFrontOnlyWaiting(fixture);
        await TickAsync(fixture);
        Assert.Equal(LoadingPhaseStates.VehicleFull, (await fixture.RuntimeAsync()).LoadingPhaseState);
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture));

        await SetSlotAvailabilityAsync(fixture, [1, 2, 3, 4], "ENABLED");
        RecordRound(fixture, Waiting(StopKind.StagingRulesOn, "W-1", "FRONT", DispatchAdmissionChain.Eligible));
        await TickAsync(fixture);
        await CloseCorrectionAsync(fixture, correctionId);
        RecordRound(fixture, Waiting(StopKind.StagingRulesOn, "W-1", "FRONT", DispatchAdmissionChain.Eligible));
        await TickAsync(fixture);

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, journey.LoadingPhaseState);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, journey.Stage);
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "A withdrawn judgement still sent the vehicle on.");
        Assert.Equal(
            LoadingPhaseStates.CargoHoldingWait,
            (await Batch7CargoHoldingTests.LoadingPhaseSnapshotsAsync(fixture))[^1].State);
    }

    /// <summary>
    /// control-server#800：离站核验一经发出，判定保持（CP-0018 第五节第二张表第 2 项；<see cref="LoadingPhaseMachine"/> 原有的保持点）：之后等着的那条变得装得下，
    /// 车照样是 <c>VEHICLE_FULL</c>、照样在等核验答复。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task OnceTheDepartureCheckIsSentTheJudgementStays()
    {
        await using RuntimeFixture fixture = await LoadedAsync(StopKind.StagingRulesOn);
        await FillFrontAsync(fixture);
        RecordFrontOnlyWaiting(fixture);
        await TickAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await fixture.RuntimeAsync()).Stage);

        await SetSlotAvailabilityAsync(fixture, [1, 2, 3, 4], "ENABLED");
        for (int round = 0; round < 2; round++)
        {
            RecordRound(fixture, Waiting(StopKind.StagingRulesOn, "W-1", "FRONT", DispatchAdmissionChain.Eligible));
            await TickAsync(fixture);
        }

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(LoadingPhaseStates.VehicleFull, journey.LoadingPhaseState);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, journey.Stage);
    }

    /// <summary>
    /// control-server#800（cs#780 独立审查 M1）：判满状态下在追加停靠受理了出发，没装的已经放回，离站核验还没发出。这时等着的那条已经装得下，受理之后的重判
    /// 也不撤回：本停靠已受理出发是保持点（落在停靠行的 <c>OperatorDepartedAt</c> 上），车照常离站，不等持货期限。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AnAcceptedDepartureWhileFullIsNotWithdrawnByALaterRound()
    {
        await using RuntimeFixture fixture = await FullThenAtTheAppendedStopAsync();
        // 受理的那一轮里，出发先答、装货阶段后判：前仓放开、等着的那条变得装得下，同一轮的重判就会撤回——除非受理留下了保持点。
        await SetSlotAvailabilityAsync(fixture, [1, 2, 3, 4], "ENABLED");
        string departureRequestId = await StagingDepartureRequestTests.RequestDepartureAsync(fixture);
        RecordRound(fixture, Waiting(StopKind.StagingRulesOn, "W-1", "FRONT", DispatchAdmissionChain.Eligible));
        await StagingDepartureRequestTests.RunAsync(fixture);
        Assert.Equal(
            "ACCEPTED",
            (await StagingDepartureRequestTests.ResultAsync(fixture, departureRequestId)).GetProperty("outcome").GetString());
        Assert.NotNull((await Batch7StopDrivenAdvanceDriver.CurrentStopAsync(fixture, Demand)).OperatorDepartedAt);
        Assert.Equal(LoadingPhaseStates.VehicleFull, (await fixture.RuntimeAsync()).LoadingPhaseState);

        RecordRound(fixture, Waiting(StopKind.StagingRulesOn, "W-1", "FRONT", DispatchAdmissionChain.Eligible));
        await StagingDepartureRequestTests.RunAsync(fixture);

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(LoadingPhaseStates.VehicleFull, journey.LoadingPhaseState);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, journey.Stage);
    }

    /// <summary>
    /// cs#800 返工（调度 10-21，分票王同步进票面验收）：等着的候选只因途中追加的 9 段上限被拒，按 CP-0018 仍算容量（协议 legs 上限是 9），车上 0 篮也判满。
    /// 但车要在原地把已承诺的需求装完才走：锚需求没装之前不发离站核验，不会空车离开。那条候选取消（下一轮不再等着）就撤回判满；再出现、锚需求装上之后，
    /// 车带着货离站。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task FullOnlyByThePlanLimitTheVehicleLoadsWhatItPromisedBeforeLeaving()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow driving = await fixture.RuntimeAsync();
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", driving.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = driving.PickupStationRiotId };
        for (int round = 0; round < 5; round++)
        {
            RecordPlanLimitWaiting(fixture);
            await TickAsync(fixture);
        }
        JourneyRuntimeRow full = await fixture.RuntimeAsync();
        Assert.Equal(LoadingPhaseStates.VehicleFull, full.LoadingPhaseState);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, full.Stage);
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "A vehicle full only on the plan limit left with nothing on board.");

        RecordRound(fixture);
        await TickAsync(fixture);
        JourneyRuntimeRow withdrawn = await fixture.RuntimeAsync();
        Assert.NotEqual(LoadingPhaseStates.VehicleFull, withdrawn.LoadingPhaseState);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, withdrawn.Stage);

        RecordPlanLimitWaiting(fixture);
        await TickAsync(fixture);
        Assert.Equal(LoadingPhaseStates.VehicleFull, (await fixture.RuntimeAsync()).LoadingPhaseState);
        RecordPlanLimitWaiting(fixture);
        await Batch7StopDrivenAdvanceDriver.EnterSublotAsync(
            fixture, Demand, "SUBLOT-001", Batch7StopDrivenAdvanceDriver.FirstSubmissionId);
        await fixture.ApplySafeResultAsync(
            await fixture.OperationAsync(SlotOperationType.Load), SlotOperationType.Load, SlotBusinessState.Occupied);
        RecordPlanLimitWaiting(fixture);
        await TickAsync(fixture);

        JourneyRuntimeRow left = await fixture.RuntimeAsync();
        Assert.Equal(JourneyDemandStatuses.Loaded, (await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == Demand, Token)).Status);
        Assert.Equal(LoadingPhaseStates.VehicleFull, left.LoadingPhaseState);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, left.Stage);
    }

    /// <summary>
    /// 现场回放（v5 生产 2026-10-21 14:31:59，agv02 旅程 2413d3b7，读库坐实见 cs#800 评论）：车到 217 时一篮没装，只挂一条预留未装的需求；同站等着的候选
    /// 因本车占位被拒（那一侧扣掉预留不够它的篮数，扣掉已装还够）。旧口径判 <c>VEHICLE_FULL</c>，清单一装完车就自己走（只带 3 篮）。新口径：只被预留挡住，不计入，
    /// 不判满，车持货等单。夹具的区表只有前仓：锚需求预留前仓一篮，候选要前仓四篮。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task TheAgv02Run1431ReplayedNothingLoadedAndTheCandidateOnlyBlockedByAReservationIsNotFull()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal((3, 4), await DispatchFreeSlotsAsync(fixture));
        JourneyRuntimeRow driving = await fixture.RuntimeAsync();
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", driving.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = driving.PickupStationRiotId };

        for (int round = 0; round < 3; round++)
        {
            RecordRound(fixture, Waiting(StopKind.StagingRulesOn, "W-1", "FRONT", DispatchReasonCodes.SlotGroupOccupiedByOwnCargo, baskets: 4));
            await TickAsync(fixture);
        }

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, journey.Stage);
        Assert.NotEqual(LoadingPhaseStates.VehicleFull, journey.LoadingPhaseState);
        Assert.Equal("[]", journey.FullSlotPositionsJson);
    }

    /// <summary>
    /// 现场回放（v5 生产 2026-10-21 13:51:14，agv02 旅程 cc60c059）：一侧已装 3 篮、只剩 1 格，另一侧挂着一条 2 篮预留未装的需求；走的是「等着的候选全部装不下」那一支，
    /// 读库没坐实候选是被已装货物挡住还是被预留挡住，两种都回放。夹具：前仓可用两格、已装一格（只剩 1 格），后仓四格、预留两格。
    /// 候选要前仓两篮：被已装货物挡住，照旧判满、立即离站。候选要后仓三篮：只被预留挡住，不计入，不判满。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    [InlineData("FRONT", 2, true)]
    [InlineData("REAR", 3, false)]
    public async Task TheAgv02Run1351ReplayedOnlyACandidateBlockedByLoadedCargoMakesItLeave(string side, int baskets, bool full)
    {
        await using RuntimeFixture fixture = await LoadedAsync(StopKind.StagingRulesOn);
        int[] own = await OwnSlotsAsync(fixture);
        await DisableSlotsAsync(fixture, [.. Enumerable.Range(1, 4).Except(own).Skip(1)]);
        fixture.Catalog.Set(StagingDemand(fixture), StagingDemand(fixture, SecondDemand, SecondSublot, Now.AddMinutes(-9)));
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        Assert.Equal((1, 2), await DispatchFreeSlotsAsync(fixture));

        RecordRound(fixture, Waiting(StopKind.StagingRulesOn, "W-1", side, DispatchReasonCodes.SlotGroupOccupiedByOwnCargo, baskets));
        await TickAsync(fixture);

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(full, journey.LoadingPhaseState == LoadingPhaseStates.VehicleFull);
        Assert.Equal(full ? "[\"FRONT\"]" : "[]", journey.FullSlotPositionsJson);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    /// <summary>上一轮派车只问到一条等着的候选：要前仓一篮，因本车占位被拒。</summary>
    private static void RecordFrontOnlyWaiting(RuntimeFixture fixture) =>
        RecordRound(fixture, Waiting(StopKind.StagingRulesOn, "W-1", "FRONT", DispatchReasonCodes.SlotGroupOccupiedByOwnCargo));

    /// <summary>上一轮派车只问到一条等着的候选：因途中追加的 9 段上限被拒（容量，与仓位无关）。</summary>
    private static void RecordPlanLimitWaiting(RuntimeFixture fixture) =>
        RecordRound(fixture, Waiting(StopKind.StagingRulesOn, "W-1", "FRONT", DispatchReasonCodes.EnRouteAppendPlanLimitReached));

    /// <summary>
    /// 把车报的会话基线里这几个仓位改成有货（<c>physicalState = OCCUPIED</c>）：会话在车上有货时建立，这几个仓不在基线里（OnboardDispatchFactsReader 只收
    /// 会话建立时为空的仓）。装货途中重连就是这个样子。
    /// </summary>
    private static async Task SetSlotsOccupiedInTheSessionBaselineAsync(RuntimeFixture fixture, int[] slots)
    {
        ProtocolInboxRow[] rows = await fixture.Context.ProtocolInbox
            .Where(row => row.MessageType == "CapabilitySnapshot" || row.MessageType == "SafetyStateSnapshot")
            .ToArrayAsync(Token);
        foreach (ProtocolInboxRow row in rows)
        {
            JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
            foreach (JsonNode? slot in root["payload"]!["slotStates"]!.AsArray())
            {
                if (slots.Contains(slot!["slotNo"]!.GetValue<int>()))
                {
                    slot["physicalState"] = "OCCUPIED";
                }
            }
            row.RequestJson = root.ToJsonString();
        }
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>派车按已装或已预留核对的两侧空仓（<see cref="JourneyAwareSlotLedger"/>，派车容量那一条读的同一本账）：前仓、后仓。</summary>
    private static async Task<(int Front, int Rear)> DispatchFreeSlotsAsync(RuntimeFixture fixture)
    {
        string agvId = fixture.Options.AgvId;
        OnboardDispatchFacts? onboard = await new OnboardDispatchFactsReader(
                fixture.Context, Microsoft.Extensions.Options.Options.Create(fixture.Options), fixture.Clock)
            .ReadOnboardFactsAsync(agvId, Token);
        VehicleSlotPositions? positions = await new VehicleSlotPositionReader(fixture.Context).ReadAsync(agvId, Token);
        JourneyAwareSlotLedger ledger = new(fixture.Context);
        return (
            await ledger.ReadAvailableSlotCountAsync(agvId, onboard, positions, "FRONT", Token),
            await ledger.ReadAvailableSlotCountAsync(agvId, onboard, positions, "REAR", Token));
    }

    /// <summary>
    /// 判为 <c>VEHICLE_FULL</c> 之后同站追加一条（新开第 2 个停靠），车离开第 1 停靠、「到」第 2 停靠，停在等录入（第 2 停靠上一篮没装）。每一轮之前都重写一遍读口：
    /// 判定每轮重判（control-server#800），派车轮在每一轮之后会用夹具里真裁决把读口换掉。
    /// <paramref name="loadedBeforeFull"/> 为真：装上一篮、前仓被已装货物占满、等着的那条只要前仓，判满（control-server#780 那一形，10-19）。为假：到站时一篮没装，
    /// 等着的那条只因途中追加的 9 段上限被拒（容量，与仓位无关），0 篮也判满，锚需求在判满之后装上（10-20 那一形；cs#800 独立审查 L5：这个形状靠计划上限
    /// 或整趟停靠总数上限仍然到得了）。
    /// </summary>
    private static async Task<RuntimeFixture> FullThenAtTheAppendedStopAsync(bool loadedBeforeFull = true)
    {
        const string correctionId = "70000000-0000-4000-8000-000000000780";
        Action<RuntimeFixture> recordWaiting = loadedBeforeFull ? RecordFrontOnlyWaiting : RecordPlanLimitWaiting;
        RuntimeFixture fixture;
        if (loadedBeforeFull)
        {
            fixture = await LoadedAsync(StopKind.StagingRulesOn);
            await FillFrontAsync(fixture);
            // 判满的那一轮车就会走：开着一个纠错把它扣在本站，追加进来之后再关（与上面那条「判满后追加」同一个做法）。
            await OpenCorrectionAsync(fixture, correctionId);
            recordWaiting(fixture);
            await TickAsync(fixture);
        }
        else
        {
            fixture = await ArrangeAsync(StopKind.StagingRulesOn);
            await AllowEnRouteAppendAsync(fixture);
            await fixture.Engine.ExecuteOnceAsync(Token);
            JourneyRuntimeRow driving = await fixture.RuntimeAsync();
            fixture.Riot.SetSuccessfulArrival("TO_PICKUP", driving.PickupStationRiotId);
            fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = driving.PickupStationRiotId };
            for (int round = 0; round < 2; round++)
            {
                recordWaiting(fixture);
                await TickAsync(fixture);
            }
            Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
            Assert.Empty(await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
                .Where(row => row.Status == JourneyDemandStatuses.Loaded)
                .ToArrayAsync(Token));
        }
        Assert.Equal(LoadingPhaseStates.VehicleFull, (await fixture.RuntimeAsync()).LoadingPhaseState);

        fixture.Catalog.Set(StagingDemand(fixture), StagingDemand(fixture, SecondDemand, SecondSublot, Now.AddMinutes(-9)));
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        string secondStop = (await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == SecondDemand, Token)).PickupStopId;
        Assert.NotEqual(secondStop, (await Batch7StopDrivenAdvanceDriver.CurrentStopAsync(fixture, Demand)).StopId);

        if (loadedBeforeFull)
        {
            await CloseCorrectionAsync(fixture, correctionId);
        }
        else
        {
            recordWaiting(fixture);
            await Batch7StopDrivenAdvanceDriver.EnterSublotAsync(
                fixture, Demand, "SUBLOT-001", Batch7StopDrivenAdvanceDriver.FirstSubmissionId);
            await fixture.ApplySafeResultAsync(
                await fixture.OperationAsync(SlotOperationType.Load), SlotOperationType.Load, SlotBusinessState.Occupied);
        }
        recordWaiting(fixture);
        await TickAsync(fixture);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == Demand, Token)).Status);
        Assert.Equal([StagingStationName], await DepartureTargetsAsync(fixture));

        recordWaiting(fixture);
        await Batch7StopDrivenAdvanceDriver.AnswerDepartureSafetyAsync(fixture, Demand, Batch7StopDrivenAdvanceDriver.FirstSafetyResultId);
        recordWaiting(fixture);
        await Batch7StopDrivenAdvanceDriver.ArriveAtCurrentStopAsync(fixture, Demand, "TO_GATE");
        Assert.Equal(secondStop, (await Batch7StopDrivenAdvanceDriver.CurrentStopAsync(fixture, Demand)).StopId);
        JourneyRuntimeRow atSecond = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, atSecond.Stage);
        Assert.Equal(LoadingPhaseStates.VehicleFull, atSecond.LoadingPhaseState);
        await fixture.ProveSlotDoorsClosedAsync();
        return fixture;
    }

    /// <summary>判满之后受理的出发：答复 <c>ACCEPTED</c>，没装的那条放回，装货阶段不改判、这一轮不另暂存业务快照，发给车的每一行都在发件箱里；车照常进离站。</summary>
    private static async Task AssertFullDepartureAcceptedAsync(RuntimeFixture fixture, string departureRequestId, int businessBefore)
    {
        Assert.Equal(
            "ACCEPTED",
            (await StagingDepartureRequestTests.ResultAsync(fixture, departureRequestId)).GetProperty("outcome").GetString());
        Assert.NotNull((await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == SecondDemand, Token)).RemovedAt);
        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        AssertNeverBlocked(journey);
        Assert.Equal(LoadingPhaseStates.VehicleFull, journey.LoadingPhaseState);
        Assert.Equal(businessBefore, await BusinessStatesOnFileAsync(fixture));
        await AssertEverySentLineIsOnFileAsync(fixture);

        await StagingDepartureRequestTests.RunAsync(fixture);
        AssertNeverBlocked(await fixture.RuntimeAsync());
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await fixture.RuntimeAsync()).Stage);
        await AssertEverySentLineIsOnFileAsync(fixture);
    }

    private static Task<int> BusinessStatesOnFileAsync(RuntimeFixture fixture) =>
        fixture.Context.ProtocolOutbox.AsNoTracking().CountAsync(row => row.MessageType == "VehicleBusinessStateSnapshot", Token);

    /// <summary>车载端收到的每一行报文，发件箱里都有同一个 messageId 的那一行。</summary>
    private static async Task AssertEverySentLineIsOnFileAsync(RuntimeFixture fixture)
    {
        string[] onFile = await fixture.Context.ProtocolOutbox.AsNoTracking().Select(row => row.MessageId).ToArrayAsync(Token);
        Assert.All(SentMessageIds(fixture, 0), id => Assert.Contains(id, onFile));
    }

    private static string[] SentMessageIds(RuntimeFixture fixture, int from) =>
    [
        .. fixture.Peer.Lines.Skip(from).Select(line =>
        {
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("messageId").GetString()!;
        })
    ];

    private static async Task<ProtocolOutboxRow[]> StagedLoadingPhaseRowsAsync(RuntimeFixture fixture) =>
    [
        .. (await fixture.Context.ProtocolOutbox.AsNoTracking()
                .Where(row => row.MessageType == "VehicleBusinessStateSnapshot")
                .ToArrayAsync(Token))
            .Where(row => ClosedReasonOf(row.PayloadJson) is not null)
    ];

    private static string? ClosedReasonOf(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement phase = document.RootElement.GetProperty("payload").GetProperty("loadingPhase");
        return phase.ValueKind == JsonValueKind.Object && phase.GetProperty("closedReason").ValueKind == JsonValueKind.String
            ? phase.GetProperty("closedReason").GetString()
            : null;
    }

    private static long RevisionOf(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("payload").GetProperty("vehicleBusinessStateRevision").GetInt64();
    }

    /// <summary>一条 STAGING_TO_WIRE 需求，与 <see cref="StagingPickupNoCountdownTests.ArrangeAsync"/> 放进目录的那一条同一个写法；不给参数就是那一条。</summary>
    private static AcceptedDemandSnapshot StagingDemand(
        RuntimeFixture fixture, string demandId = Demand, string sublot = "SUBLOT-001", DateTimeOffset? createdAt = null) =>
        fixture.Demand(demandId, sublot, createdAt ?? Now.AddMinutes(-10)) with
        {
            WorkType = TransportTaskTypes.StagingToWire,
            TransportDemandKey = $"{sublot}|{TransportTaskTypes.StagingToWire}",
        };

    private static void AssertNeverBlocked(JourneyRuntimeRow journey) =>
        Assert.NotEqual(JourneyRuntimeStage.Blocked, journey.Stage);

    /// <summary>发给车的每一张离站核验开往哪一站，按入队先后。</summary>
    private static async Task<string[]> DepartureTargetsAsync(RuntimeFixture fixture) =>
    [
        .. (await fixture.Context.ProtocolOutbox.AsNoTracking()
                .Where(row => row.MessageType == "PreDepartureSafetyCheck")
                .ToArrayAsync(Token))
            .OrderBy(row => row.CreatedAt)
            .ThenBy(row => row.MessageId, StringComparer.Ordinal)
            .Select(row =>
            {
                using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
                return document.RootElement.GetProperty("payload").GetProperty("targetStationId").GetString()!;
            })
    ];

    private static async Task OpenCorrectionAsync(RuntimeFixture fixture, string workflowId)
    {
        JourneyDemandRow membership = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == Demand, Token);
        fixture.Context.RecoveryWorkflows.Add(new RecoveryWorkflowRow
        {
            WorkflowId = workflowId,
            WorkflowType = "LOAD_CORRECTION",
            AgvId = fixture.Options.AgvId,
            DemandId = Demand,
            SlotOperationAttemptId = membership.LoadSlotOperationAttemptId,
            SlotsJson = membership.TargetSlotsJson,
            State = RecoveryWorkflowState.CommandPending,
            RequestMessageId = JourneyPlanBuilder.StableGuid(workflowId, "request"),
            RequestContentHash = new string('f', 64),
            CreatedAt = fixture.Clock.GetUtcNow(),
            UpdatedAt = fixture.Clock.GetUtcNow()
        });
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    private static async Task CloseCorrectionAsync(RuntimeFixture fixture, string workflowId)
    {
        RecoveryWorkflowRow correction = await fixture.Context.RecoveryWorkflows
            .SingleAsync(row => row.WorkflowId == workflowId, Token);
        correction.State = RecoveryWorkflowState.Reconciled;
        correction.UpdatedAt = fixture.Clock.GetUtcNow();
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>本区允许途中追加（持货等单适用），这条需求到站、录入、装货落定，停在离站等待。</summary>
    private static async Task<RuntimeFixture> LoadedAsync(StopKind kind)
    {
        RuntimeFixture fixture = await ArrangeAsync(kind);
        await AllowEnRouteAppendAsync(fixture);
        await LoadTheBasketAsync(fixture);
        JourneyRuntimeRow loaded = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, loaded.Stage);
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, loaded.LoadingPhaseState);
        await fixture.ProveSlotDoorsClosedAsync();
        return fixture;
    }

    /// <summary>
    /// 推进一轮。钟只拨一秒、先给一条心跳：离站核验要车在线，持货与离站等待都是五分钟，一秒之内哪一个都不会到。
    /// </summary>
    private static async Task TickAsync(RuntimeFixture fixture)
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
    }

    private static async Task<int[]> OwnSlotsAsync(RuntimeFixture fixture) =>
        JsonSerializer.Deserialize<int[]>((await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == Demand, Token)).TargetSlotsJson)!;

    /// <summary>前仓（1～4）除这条需求自己的货之外全部禁用：前仓按账本无空仓，后仓四个全空。</summary>
    private static async Task FillFrontAsync(RuntimeFixture fixture)
    {
        int[] own = await OwnSlotsAsync(fixture);
        Assert.All(own, slot => Assert.InRange(slot, 1, 4));
        await DisableSlotsAsync(fixture, [.. Enumerable.Range(1, 4).Except(own)]);
    }

    /// <summary>
    /// 把车报的会话基线里这几个仓位改成禁用（<c>Batch7CargoHoldingTests.LeaveOnlyTheFirstDemandsSlotsAsync</c> 同一个做法）：账本按它扣空仓。
    /// </summary>
    private static Task DisableSlotsAsync(RuntimeFixture fixture, int[] slots) =>
        SetSlotAvailabilityAsync(fixture, slots, "DISABLED");

    private static async Task SetSlotAvailabilityAsync(RuntimeFixture fixture, int[] slots, string availability)
    {
        ProtocolInboxRow[] rows = await fixture.Context.ProtocolInbox
            .Where(row => row.MessageType == "CapabilitySnapshot" || row.MessageType == "SafetyStateSnapshot")
            .ToArrayAsync(Token);
        foreach (ProtocolInboxRow row in rows)
        {
            JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
            foreach (JsonNode? slot in root["payload"]!["slotStates"]!.AsArray())
            {
                if (slots.Contains(slot!["slotNo"]!.GetValue<int>()))
                {
                    slot["administrativeAvailability"] = availability;
                }
            }
            row.RequestJson = root.ToJsonString();
        }
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>一条在这种停靠的站上取货、任务类型与它相同的候选：待送点停靠是待送站与 STAGING_TO_WIRE，机台停靠是 N1-1 与 WIRE_TO_GATE。</summary>
    private static DispatchCandidateVerdict Waiting(
        StopKind kind, string demandId, string? side, string reason, int baskets = 1) =>
        kind == StopKind.MachineRulesOn
            ? Verdict(demandId, TransportTaskTypes.WireToGate, MachineStationRiotId, side, reason, baskets)
            : Verdict(demandId, TransportTaskTypes.StagingToWire, StagingStationRiotId, side, reason, baskets);

    /// <summary>
    /// 一条候选的裁决。<paramref name="pickupStationRiotId"/> 为空就是没走到站点解析（Order 60）的候选，<paramref name="side"/> 为空就是没走到区表查找的。
    /// <paramref name="baskets"/> 是仓位容量那一条算出来的篮数（真派车轮在判容量之前就已写上），待送点判「只因预留装不下」时读它（control-server#800）。
    /// </summary>
    internal static DispatchCandidateVerdict Verdict(
        string demandId, string workType, int? pickupStationRiotId, string? side, string reason, int baskets = 1) =>
        new(
            new DispatchCandidateEvaluation(
                new AcceptedDemandSnapshot(
                    demandId, $"{demandId}|{workType}", 1, "history", 1, Now, WorkType: workType, Sublot: $"SUBLOT-{demandId}"),
                round: null!,
                vehicle: null!)
            {
                ExpectedBasketCount = baskets,
                AreaAssignment = side is null ? null : new AreaAssignment("N1-1", "ZONE", side),
                Route = pickupStationRiotId is not { } station
                    ? null
                    : new ResolvedJourneyRoute("ZONE", "route", $"STATION-{station}", station, "GATE", 210, FixedStation: null!),
            },
            reason);

    private static void RecordRound(RuntimeFixture fixture, params DispatchCandidateVerdict[] verdicts) =>
        fixture.SlotGroupFullness.Record(new DispatchRoundOutcome(
            Round: null!,
            [new DispatchVehicleOutcome(fixture.Options.AgvId, fixture.Options.VehicleKey, verdicts)]));
}
