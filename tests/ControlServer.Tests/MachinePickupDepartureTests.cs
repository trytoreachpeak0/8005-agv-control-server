using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.StagingDepartureRequestTests;
using static ControlServer.Tests.StagingPickupNoCountdownTests;

namespace ControlServer.Tests;

/// <summary>
/// 机台取货停靠装完后的「出发」（control-server#725，CP-0014 修订项一 <c>REQ-0354</c>；协议 4.0.0 候选 <c>3fb214e5</c> wire-notes
/// 「<c>CurrentStopDepartureRequested</c> and <c>CurrentStopDepartureResult</c>」第 1、4、6 条）。
/// </summary>
/// <remarks>
/// <para>
/// <b>什么时候受理</b>：本停靠已承诺的装货都已完成、本站至少装上一篮、车还没为离站发核验。两种等待都受理：装货阶段还开着（持货等单，或后面还有追加的
/// 取货停靠），等同持货期限此刻到期，装货阶段以 <c>OPERATOR_DEPARTED</c> 关闭、没开始装的按持货到期放回；装货阶段已经结束、只剩站点离站等待，
/// 装货阶段与它的 <c>closedReason</c> 一样不动。两种都不等站点离站期限的剩余部分、不留纠错宽限，经纠错闸与 <c>DEPARTURE</c> 核验立即离站。
/// </para>
/// <para>
/// <b>不受 <c>StagingDepartureRules</c> 开关控制</b>（C11 定）：持货夹具（<see cref="Batch7CargoHoldingTests.HoldingFixtureAsync"/>）与本文件里
/// 关了开关的那几格都是开关关着的。
/// </para>
/// </remarks>
public sealed class MachinePickupDepartureTests
{
    private const int SecondPickupStationRiotId = 13;

    private const string SecondPickupArea = "N1-2";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 向量 MACHINE-CARGO-HOLDING：机台取货装完第一条、后面还挂着一个追加的取货停靠（另一个机台，还没开始装），站点离站等待（5 分钟）才过了几秒。
    /// 操作员点出发：答复 <c>ACCEPTED</c>，装货阶段以 <c>OPERATOR_DEPARTED</c> 关闭，追加的那条按持货到期放回待派（仍受理、不抑制、不计站点超时次数、
    /// 它的停靠被删），已装的那条留在车上；不等站点期限，同一轮就发离站核验，核验问的是卸货站。线序：答复 → 业务快照 → 计划 → 离站核验，不发空清单。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-MACHINE-CARGO-HOLDING")]
    public async Task DepartureWhileTheLoadingPhaseIsOpenActsAsTheHoldingDeadlineAndLeavesAtOnce()
    {
        await using RuntimeFixture fixture = await Batch7CargoHoldingTests.HoldingFixtureAsync();
        Assert.False(fixture.Options.StagingDepartureRules.Enabled);
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromMinutes(5);
        await Batch7CargoHoldingTests.LoadTheFirstDemandAsync(fixture);
        fixture.BoxCounts.Set(Batch7StopDrivenAdvanceDriver.SecondSublot, 7);
        await Batch7ThreeStopJourneyTests.AppendDemandAsync(
            fixture, SecondDemandId, Batch7StopDrivenAdvanceDriver.SecondSublot, SecondPickupArea, SecondPickupStationRiotId);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow before = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, before.Stage);
        Assert.NotEqual(LoadingPhaseStates.Closed, before.LoadingPhaseState);
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture));
        await AcknowledgeEverythingSentAsync(fixture);
        int worklistsBefore = await CountAsync(fixture, "CurrentStopWorklistSnapshot");
        int wireBefore = fixture.Peer.Lines.Count;

        string departureRequestId = await RequestDepartureAsync(fixture, operationSessionId: null);
        await RunAsync(fixture);

        Assert.Equal("ACCEPTED", (await ResultAsync(fixture, departureRequestId)).GetProperty("outcome").GetString());
        JourneyRuntimeRow journey = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(
            (LoadingPhaseStates.Closed, LoadingClosedReasons.OperatorDeparted),
            (journey.LoadingPhaseState, journey.LoadingClosedReason));
        await AssertReleasedForRedispatchAsync(fixture, journey.JourneyId, SecondDemandId);
        Assert.Equal(0, await StationDeadlineReleasesAsync(fixture));
        await SuppressionAssertions.AssertNothingSuppressedAsync(fixture.Context);
        Assert.Equal(
            JourneyStopStatuses.Removed,
            (await PickupStopAtAsync(fixture, journey.JourneyId, SecondPickupStationRiotId)).Status);
        JourneyDemandRow loaded = await MembershipOnAsync(fixture, journey.JourneyId, FirstDemandId);
        Assert.Equal((JourneyDemandStatuses.Loaded, (DateTimeOffset?)null), (loaded.Status, loaded.RemovedAt));

        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, journey.Stage);
        ProtocolOutboxRow check = Assert.Single(await DepartureChecksAsync(fixture));
        Assert.Equal(TaskTypeStationRuntimeSeed.GateStationName, TargetStationOf(check));
        Assert.Equal(worklistsBefore, await CountAsync(fixture, "CurrentStopWorklistSnapshot"));

        string[] sent = SentSince(fixture, wireBefore);
        int resultAt = Array.IndexOf(sent, CurrentStopDepartures.ResultType);
        Assert.True(resultAt >= 0, string.Join(",", sent));
        Assert.Equal(
            ["VehicleBusinessStateSnapshot", "UpcomingStopPlanSnapshot", "PreDepartureSafetyCheck"],
            sent.Skip(resultAt + 1));
        Assert.Equal(
            LoadingClosedReasons.OperatorDeparted,
            (await Batch7CargoHoldingTests.LoadingPhaseSnapshotsAsync(fixture)).Last().ClosedReason);
        Assert.Contains(fixture.EngineLog.Entries, entry =>
            entry.Message.Contains(departureRequestId, StringComparison.Ordinal) &&
            entry.Message.Contains("OP-713", StringComparison.Ordinal) &&
            entry.Message.Contains("1 demand(s) released", StringComparison.Ordinal));
    }

    /// <summary>
    /// 持货等单（本站装完、计划里再没有可装的）时点出发：装货阶段从 <c>CARGO_HOLDING_WAIT</c> 以 <c>OPERATOR_DEPARTED</c> 关闭，什么也不放回，
    /// 计划没变就不重发；不等站点期限，答复之后是业务快照与离站核验。开关开着、关着一个样。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-MACHINE-CARGO-HOLDING")]
    public async Task DepartureInCargoHoldingWaitClosesTheLoadingPhaseAsOperatorDeparted(bool stagingRulesEnabled)
    {
        await using RuntimeFixture fixture = await LoadedMachineStopAsync(MachineShape.CargoHoldingWait, stagingRulesEnabled);
        int wireBefore = fixture.Peer.Lines.Count;

        string departureRequestId = await RequestDepartureAsync(fixture, operationSessionId: null);
        await RunAsync(fixture);

        Assert.Equal("ACCEPTED", (await ResultAsync(fixture, departureRequestId)).GetProperty("outcome").GetString());
        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(
            (LoadingPhaseStates.Closed, LoadingClosedReasons.OperatorDeparted),
            (journey.LoadingPhaseState, journey.LoadingClosedReason));
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, journey.Stage);
        Assert.Null((await MembershipAsync(fixture, Demand)).RemovedAt);
        string[] sent = SentSince(fixture, wireBefore);
        Assert.Equal(
            ["VehicleBusinessStateSnapshot", "PreDepartureSafetyCheck"],
            sent.Skip(Array.IndexOf(sent, CurrentStopDepartures.ResultType) + 1));
    }

    /// <summary>
    /// 向量 MACHINE-STATION-WAIT：装货阶段已经结束、只剩站点离站等待（5 分钟，才过了几秒）时点出发：<c>ACCEPTED</c>，装货阶段与 <c>closedReason</c>
    /// 原样不动、不发业务快照，答复之后直接是离站核验。两种结束法各一格：分区禁止途中追加（不持货等单）、持货期限先于站点期限到了（一站多批时站点期限
    /// 从最后一批起算，比持货期限晚）。两侧装满（<c>VEHICLE_FULL</c>）当轮就离站、不在站点离站等待里停，没有这一格。
    /// </summary>
    [Theory]
    [InlineData(MachineShape.NoHolding)]
    [InlineData(MachineShape.HoldingDeadlinePassed)]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-MACHINE-STATION-WAIT")]
    public async Task DepartureWhileOnlyTheStationWaitRemainsLeavesAtOnceAndKeepsTheClosedReason(MachineShape shape)
    {
        await using RuntimeFixture fixture = await LoadedMachineStopAsync(shape, stagingRulesEnabled: false);
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        (string? State, string? Reason) phaseBefore = (before.LoadingPhaseState, before.LoadingClosedReason);
        int businessBefore = await CountAsync(fixture, "VehicleBusinessStateSnapshot");
        int worklistsBefore = await CountAsync(fixture, "CurrentStopWorklistSnapshot");
        int wireBefore = fixture.Peer.Lines.Count;

        string departureRequestId = await RequestDepartureAsync(fixture, operationSessionId: null);
        await RunAsync(fixture);

        Assert.Equal("ACCEPTED", (await ResultAsync(fixture, departureRequestId)).GetProperty("outcome").GetString());
        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(phaseBefore, (journey.LoadingPhaseState, journey.LoadingClosedReason));
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, journey.Stage);
        Assert.Equal(businessBefore, await CountAsync(fixture, "VehicleBusinessStateSnapshot"));
        Assert.Equal(worklistsBefore, await CountAsync(fixture, "CurrentStopWorklistSnapshot"));
        string[] sent = SentSince(fixture, wireBefore);
        Assert.Equal(
            ["PreDepartureSafetyCheck"],
            sent.Skip(Array.IndexOf(sent, CurrentStopDepartures.ResultType) + 1));
    }

    /// <summary>
    /// 受理之后被门未证明扣车挡住（REQ-0364）：这一轮不发核验；扣车解除之后照样立即离站，不回头去等站点离站期限的剩余部分。车在扣车期间再点一次出发，
    /// 答「已在出发」。去掉「本站已由操作员结束」的落库（只在受理那一轮内存里记着），解除之后车要等满 5 分钟，这一条红。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-MACHINE-STATION-WAIT")]
    public async Task AnAcceptedDepartureHeldByTheDoorsLeavesOnceReleasedWithoutWaitingOutTheStationWait()
    {
        await using RuntimeFixture fixture = await LoadedMachineStopAsync(MachineShape.NoHolding, stagingRulesEnabled: false);
        SlotDoorHoldRow hold = new()
        {
            HoldId = Guid.NewGuid().ToString("D"),
            AgvId = fixture.Options.AgvId,
            DemandId = Demand,
            SlotsJson = "[1]",
            HeldAt = fixture.Clock.GetUtcNow()
        };
        fixture.Context.SlotDoorHolds.Add(hold);
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        string departureRequestId = await RequestDepartureAsync(fixture, operationSessionId: null);
        await RunAsync(fixture);
        Assert.Equal("ACCEPTED", (await ResultAsync(fixture, departureRequestId)).GetProperty("outcome").GetString());
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await fixture.RuntimeAsync()).Stage);
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture));

        string again = await RequestDepartureAsync(fixture, operationSessionId: null);
        await RunAsync(fixture);
        JsonElement second = await ResultAsync(fixture, again);
        Assert.Equal("REJECTED", second.GetProperty("outcome").GetString());
        Assert.Equal(ServerReasonCodes.ActionNotAllowedInState, second.GetProperty("problem").GetProperty("reasonCode").GetString());

        SlotDoorHoldRow tracked = await fixture.Context.SlotDoorHolds.SingleAsync(row => row.HoldId == hold.HoldId, Token);
        tracked.ReleasedAt = fixture.Clock.GetUtcNow();
        tracked.ReleasedByActionId = "release";
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        await RunAsync(fixture);

        Assert.True(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "The vehicle went back to waiting out the station wait.");
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await fixture.RuntimeAsync()).Stage);
    }

    /// <summary>
    /// 向量 MACHINE-LOADING-INCOMPLETE：本停靠装货还没完成时一律 <c>ACTION_NOT_ALLOWED_IN_STATE</c>，什么都不变——等录入（一篮没装）、正在装
    /// （命令已下、结果未到）。开关开着、关着一样。
    /// </summary>
    [Theory]
    [InlineData(LoadingIncomplete.AwaitingEntry, false)]
    [InlineData(LoadingIncomplete.AwaitingEntry, true)]
    [InlineData(LoadingIncomplete.Loading, false)]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-MACHINE-LOADING-INCOMPLETE")]
    public async Task DepartureBeforeTheStopsLoadsAreCompleteIsRefused(LoadingIncomplete when, bool stagingRulesEnabled)
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.MachineRulesOn);
        fixture.Options.StagingDepartureRules.Enabled = stagingRulesEnabled;
        await AllowEnRouteAppendAsync(fixture);
        if (when == LoadingIncomplete.AwaitingEntry)
        {
            await fixture.AdvanceToSublotWaitAsync();
        }
        else
        {
            await fixture.AdvanceToLoadResultAsync();
        }
        await fixture.ProveSlotDoorsClosedAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        long current = await CurrentStopDepartures.CurrentWorklistRevisionAsync(fixture.Context, fixture.Options.AgvId, Token);

        string departureRequestId = await RequestDepartureAsync(fixture);
        await RunAsync(fixture);

        JsonElement result = await ResultAsync(fixture, departureRequestId);
        Assert.Equal("REJECTED", result.GetProperty("outcome").GetString());
        Assert.Equal(ServerReasonCodes.ActionNotAllowedInState, result.GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Equal(current, result.GetProperty("currentWorklistRevision").GetInt64());
        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(before.Stage, journey.Stage);
        Assert.NotEqual(LoadingPhaseStates.Closed, journey.LoadingPhaseState);
        Assert.Null((await MembershipAsync(fixture, Demand)).RemovedAt);
        await SuppressionAssertions.AssertNothingSuppressedAsync(fixture.Context);
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture));
    }

    /// <summary>
    /// 向量 MACHINE-LOADING-INCOMPLETE 的「一篮没装」：第二个机台取货站上唯一那条需求在扫码前被取消（本站以空清单结束、车停在离站等待里），
    /// 车上只有第一站装的货。本站一篮没装不算装完（CP-0014 起草时定第 7 项），拒，什么都不变。只看「车上有货」的实现在这里放行。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-MACHINE-LOADING-INCOMPLETE")]
    public async Task DepartureAtAStopWhoseOnlyDemandWasCancelledBeforeLoadingIsRefused()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyStopRow secondPickup = await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture);
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromMinutes(5);
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer);
            OnboardConnectionState state = StopEndedJourneyContinuesTests.Connection(fixture);
            const string cancellationId = "d7250000-0000-4000-8000-000000000001";
            var who = new { operatorId = "OP-001", verificationMethod = "BADGE", verifiedAt = fixture.Clock.GetUtcNow() };
            await processor.ProcessAsync(
                StopEndedJourneyContinuesTests.Envelope(fixture, "d7250000-0000-4000-8000-000000000002", "LoadCancellationStartRequested", new
                {
                    cancellationId,
                    demandId = SecondDemandId,
                    slotOperationAttemptId = (string?)null,
                    @operator = who,
                    reason = "Nothing to load at this stop."
                }),
                state,
                Token);
            await processor.ProcessAsync(
                StopEndedJourneyContinuesTests.Envelope(fixture, "d7250000-0000-4000-8000-000000000003", "LoadCancellationResult", new
                {
                    cancellationId,
                    demandId = SecondDemandId,
                    slotOperationAttemptId = (string?)null,
                    overallOutcome = "ALL_EMPTY",
                    slotResults = Array.Empty<object>(),
                    observedAt = fixture.Clock.GetUtcNow()
                }),
                state,
                Token);
        }
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow before = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, before.Stage);
        Assert.Equal(secondPickup.StopId, (await CurrentStopAsync(fixture, FirstDemandId)).StopId);
        Assert.Equal(JourneyDemandStatuses.Terminated, (await MembershipAsync(fixture, SecondDemandId)).Status);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, FirstDemandId)).Status);
        int checksBefore = await Batch7CargoHoldingTests.DepartureChecksSentAsync(fixture);

        string departureRequestId = await RequestDepartureAsync(fixture, operationSessionId: null);
        await RunAsync(fixture);

        JsonElement result = await ResultAsync(fixture, departureRequestId);
        Assert.Equal("REJECTED", result.GetProperty("outcome").GetString());
        Assert.Equal(ServerReasonCodes.ActionNotAllowedInState, result.GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Equal(checksBefore, await Batch7CargoHoldingTests.DepartureChecksSentAsync(fixture));
        Assert.Equal((before.LoadingPhaseState, before.LoadingClosedReason),
            ((await JourneyOfAsync(fixture, FirstDemandId)).LoadingPhaseState, (await JourneyOfAsync(fixture, FirstDemandId)).LoadingClosedReason));
        Assert.Null((await MembershipAsync(fixture, FirstDemandId)).RemovedAt);
    }

    /// <summary>
    /// 本站装完、但装货更正（纠错）开着：拒，不改装货阶段、不发核验。按了出发就不再有纠错时间，所以已经开着的纠错要先收敛（CP-0014 说明 2）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task DepartureWhileACorrectionIsOpenIsRefused()
    {
        await using RuntimeFixture fixture = await LoadedMachineStopAsync(MachineShape.CargoHoldingWait, stagingRulesEnabled: false);
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            string answer = await fixture.RequestLoadCorrectionOnConnectionAsync(
                connection,
                Guid.NewGuid().ToString("D"),
                Demand,
                load.SlotOperationAttemptId,
                JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!);
            Assert.DoesNotContain("LoadCorrectionRejected", answer, StringComparison.Ordinal);
        }

        string departureRequestId = await RequestDepartureAsync(fixture, operationSessionId: null);
        await RunAsync(fixture);

        JsonElement result = await ResultAsync(fixture, departureRequestId);
        Assert.Equal("REJECTED", result.GetProperty("outcome").GetString());
        Assert.Equal(ServerReasonCodes.ActionNotAllowedInState, result.GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Equal(LoadingPhaseStates.CargoHoldingWait, (await fixture.RuntimeAsync()).LoadingPhaseState);
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture));
    }

    /// <summary>
    /// 规则本身（<see cref="CurrentStopDepartureRule"/>）：机台取货停靠只在「本站装完、本站至少一篮」时受理；其余状态检查与待送点同一套，先后不变
    /// （站 → 版本 → 状态）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public void AtAMachinePickupTheRuleAcceptsOnlyOnceTheStopsLoadsAreComplete()
    {
        CurrentStopDepartures.Request request = new("m", "AGV", "d", null, "N1-1", 7, "OP", DateTimeOffset.UnixEpoch);
        CurrentStopDepartureRule.Facts machine = new(
            "N1-1", "SESSION", 7, false, true, false, false, false, false, false, MachinePickupLoadsComplete: true);

        Assert.Null(CurrentStopDepartureRule.Decide(request, machine));
        Assert.Equal(ServerReasonCodes.StationMismatch,
            CurrentStopDepartureRule.Decide(request with { StationId = "305", WorklistRevision = 3 }, machine with { MachinePickupLoadsComplete = false })!.ReasonCode);
        Assert.Equal(ServerReasonCodes.WorklistRevisionStale,
            CurrentStopDepartureRule.Decide(request with { WorklistRevision = 6 }, machine with { MachinePickupLoadsComplete = false })!.ReasonCode);
        foreach (CurrentStopDepartureRule.Facts refused in new[]
                 {
                     machine with { MachinePickupLoadsComplete = false },
                     machine with { AtStopAwaitingDeparture = false },
                     machine with { SlotOperationRunning = true },
                     machine with { CorrectionOrCancellationOpen = true },
                     machine with { AlreadyDeparting = true },
                 })
        {
            Assert.Equal(ServerReasonCodes.ActionNotAllowedInState, CurrentStopDepartureRule.Decide(request, refused)!.ReasonCode);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    /// <summary>机台取货停靠装完一篮之后，车停在离站等待里的几种样子。</summary>
    public enum MachineShape
    {
        /// <summary>本区允许途中追加，持货等单：<c>CARGO_HOLDING_WAIT</c>。</summary>
        CargoHoldingWait,

        /// <summary>本区禁止途中追加，不持货等单：装货阶段已结束（落库 <c>CLOSED</c>／<c>PLANNED_LOADING_COMPLETE</c>，不发给车）。</summary>
        NoHolding,

        /// <summary>持货期限（2 分钟）先到，站点离站期限（5 分钟）还没到：<c>CLOSED</c>／<c>CARGO_HOLDING_TIMEOUT</c>。</summary>
        HoldingDeadlinePassed
    }

    /// <summary>本停靠装货还没完成的两种样子。</summary>
    public enum LoadingIncomplete
    {
        AwaitingEntry,
        Loading
    }

    /// <summary>WIRE_TO_GATE 在机台 N1-1 装上一篮，停在离站等待里（站点期限 5 分钟还剩大半），门已证明关着。</summary>
    private static async Task<RuntimeFixture> LoadedMachineStopAsync(MachineShape shape, bool stagingRulesEnabled)
    {
        RuntimeFixture fixture = await ArrangeAsync(StopKind.MachineRulesOn);
        fixture.Options.StagingDepartureRules.Enabled = stagingRulesEnabled;
        if (shape != MachineShape.NoHolding)
        {
            await AllowEnRouteAppendAsync(fixture);
        }
        if (shape == MachineShape.HoldingDeadlinePassed)
        {
            fixture.Options.CargoHoldingTimeout = TimeSpan.FromMinutes(2);
        }
        await LoadTheBasketAsync(fixture);
        await fixture.ProveSlotDoorsClosedAsync();
        if (shape == MachineShape.HoldingDeadlinePassed)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(2));
            await RunAsync(fixture);
            await fixture.ProveSlotDoorsClosedAsync();
        }
        await RunAsync(fixture);

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, journey.Stage);
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "The station wait was already over.");
        (string? State, string? Reason) expected = shape switch
        {
            MachineShape.CargoHoldingWait => (LoadingPhaseStates.CargoHoldingWait, null),
            MachineShape.NoHolding => (LoadingPhaseStates.Closed, LoadingClosedReasons.PlannedLoadingComplete),
            _ => (LoadingPhaseStates.Closed, LoadingClosedReasons.CargoHoldingTimeout)
        };
        Assert.Equal(expected, (journey.LoadingPhaseState, journey.LoadingClosedReason));
        await AcknowledgeEverythingSentAsync(fixture);
        return fixture;
    }

    /// <summary>
    /// 车确认了此前收到的每一行（真车载端逐条回 <c>SnapshotAppliedAck</c>／<c>DurableAck</c>）。夹具的对端不回确认，不标这一下，每轮顶上的补发与
    /// 空清单的补发会把旧行再发一遍，夹在答复之后的线序里。
    /// </summary>
    private static async Task AcknowledgeEverythingSentAsync(RuntimeFixture fixture)
    {
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        foreach (ProtocolOutboxRow row in await fixture.Context.ProtocolOutbox.Where(row => row.AcknowledgedAt == null).ToArrayAsync(Token))
        {
            row.AcknowledgedAt = now;
        }
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    private static string[] SentSince(RuntimeFixture fixture, int lineIndex) =>
        [.. fixture.Peer.Lines.Skip(lineIndex).Select(line =>
        {
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("messageType").GetString()!;
        })];

    private static async Task AssertReleasedForRedispatchAsync(RuntimeFixture fixture, string journeyId, string demandId)
    {
        Assert.Equal(
            DemandExecutionStatus.Accepted,
            (await fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(row => row.DemandId == demandId, Token)).Status);
        JourneyDemandRow membership = await MembershipOnAsync(fixture, journeyId, demandId);
        Assert.True(membership.RemovedAt is not null, $"{demandId} is still carried by {journeyId} ({membership.Status}).");
        Assert.Equal(DemandJourneyLookup.ReleasedForRedispatchReason, membership.RemovalReason);
        Assert.True(
            await DemandJourneyLookup.ReleasedForRedispatch(fixture.Context).AsNoTracking().AnyAsync(row => row.DemandId == demandId, Token),
            $"{demandId} is not offered to the dispatch round as released for redispatch.");
    }

    private static Task<JourneyDemandRow> MembershipAsync(RuntimeFixture fixture, string demandId) =>
        fixture.Context.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(row => row.DemandId == demandId, Token);

    private static Task<JourneyDemandRow> MembershipOnAsync(RuntimeFixture fixture, string journeyId, string demandId) =>
        fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == journeyId && row.DemandId == demandId, Token);

    private static Task<JourneyStopRow> PickupStopAtAsync(RuntimeFixture fixture, string journeyId, int stationRiotId) =>
        fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(
                row => row.JourneyId == journeyId && row.StopRole == JourneyStopRoles.Pickup && row.StationRiotId == stationRiotId,
                Token);

    private static Task<int> StationDeadlineReleasesAsync(RuntimeFixture fixture) =>
        fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .CountAsync(row => row.RemovalReason == DemandJourneyLookup.ReleasedAtStationDeadlineReason, Token);

    private static Task<int> CountAsync(RuntimeFixture fixture, string messageType) =>
        fixture.Context.ProtocolOutbox.AsNoTracking().CountAsync(row => row.MessageType == messageType, Token);

    private static Task<ProtocolOutboxRow[]> DepartureChecksAsync(RuntimeFixture fixture) =>
        fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "PreDepartureSafetyCheck")
            .ToArrayAsync(Token);

    private static string? TargetStationOf(ProtocolOutboxRow check)
    {
        using JsonDocument document = JsonDocument.Parse(check.PayloadJson);
        return document.RootElement.GetProperty("payload").GetProperty("targetStationId").GetString();
    }
}
