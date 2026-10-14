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

    /// <summary>验收 4：前仓满、后仓只剩 1 个空仓，等着的只有一条要后仓 2 篮（那一侧空仓不够）：立即离站。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task TheOnlyWaitingDemandNeedingMoreThanTheOneFreeRearSlotDeparts()
    {
        await using RuntimeFixture fixture = await LoadedAsync(StopKind.StagingRulesOn);
        await FillFrontAsync(fixture);
        await DisableSlotsAsync(fixture, [5, 6, 7]);

        RecordRound(fixture, Waiting(StopKind.StagingRulesOn, "W-1", "REAR", DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable));
        await TickAsync(fixture);

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(LoadingPhaseStates.VehicleFull, journey.LoadingPhaseState);
        Assert.Equal("[\"FRONT\"]", journey.FullSlotPositionsJson);
        Assert.True(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "A demand that cannot fit held the vehicle.");
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
    /// 验收 9 与锁：判满的这一轮已经开着纠错。装货阶段照样判成 <c>VEHICLE_FULL</c>，但纠错挡车，不发核验。纠错期间等着的需求变了（后仓来了一条装得下的），
    /// 判定锁住不翻回。纠错闭环之后的那一轮立即走，不再给宽限。
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
        Assert.Equal(LoadingPhaseStates.VehicleFull, (await fixture.RuntimeAsync()).LoadingPhaseState);
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture));

        RecoveryWorkflowRow correction = await fixture.Context.RecoveryWorkflows
            .SingleAsync(row => row.WorkflowId == "70000000-0000-4000-8000-000000000710", Token);
        correction.State = RecoveryWorkflowState.Reconciled;
        correction.UpdatedAt = fixture.Clock.GetUtcNow();
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        RecordRound(fixture, Waiting(StopKind.StagingRulesOn, "W-2", "REAR", DispatchAdmissionChain.Eligible));
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
    [InlineData("FRONT", new[] { 1, 2, 3, 4 })]
    [InlineData("REAR", new[] { 5, 6, 7, 8 })]
    public async Task AStagingSublotOpensOnlyTheSideItsAreaIsAssigned(string side, int[] sideSlots)
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
        Assert.All(opened, slot => Assert.Contains(slot, sideSlots));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

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
    private static async Task DisableSlotsAsync(RuntimeFixture fixture, int[] slots)
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
                    slot["administrativeAvailability"] = "DISABLED";
                }
            }
            row.RequestJson = root.ToJsonString();
        }
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>一条在这种停靠的站上取货、任务类型与它相同的候选：待送点停靠是待送站与 STAGING_TO_WIRE，机台停靠是 N1-1 与 WIRE_TO_GATE。</summary>
    private static DispatchCandidateVerdict Waiting(StopKind kind, string demandId, string? side, string reason) =>
        kind == StopKind.MachineRulesOn
            ? Verdict(demandId, TransportTaskTypes.WireToGate, MachineStationRiotId, side, reason)
            : Verdict(demandId, TransportTaskTypes.StagingToWire, StagingStationRiotId, side, reason);

    /// <summary>
    /// 一条候选的裁决。<paramref name="pickupStationRiotId"/> 为空就是没走到站点解析（Order 60）的候选，<paramref name="side"/> 为空就是没走到区表查找的。
    /// </summary>
    internal static DispatchCandidateVerdict Verdict(
        string demandId, string workType, int? pickupStationRiotId, string? side, string reason) =>
        new(
            new DispatchCandidateEvaluation(
                new AcceptedDemandSnapshot(
                    demandId, $"{demandId}|{workType}", 1, "history", 1, Now, WorkType: workType, Sublot: $"SUBLOT-{demandId}"),
                round: null!,
                vehicle: null!)
            {
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
