using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.StopEndedJourneyContinuesTests;
using static ControlServer.Tests.UnloadCrashResumeTests;

namespace ControlServer.Tests;

/// <summary>
/// 多需求旅程里，异常处置会话终结了旅程被阻塞在其上的那一条需求、而旅程还带着别的需求时，旅程从 <c>Blocked</c> 放出来接着走
/// （control-server#499）。
/// </summary>
/// <remarks>
/// <para>
/// 修之前：恢复协调器经 <c>PickupStopTermination</c> 终结那一条，旅程因还带着别的需求而不收尾，阶段却一直留在 <c>Blocked</c>。
/// 引擎遇到 <c>Blocked</c> 每轮直接返回，能让旅程离开它的只有收尾与修复续行的结果——所以车上剩下的货永远不卸，只能改库。
/// </para>
/// <para>
/// 三类来路各一条以上：取货停靠上的装货补偿、卸货停靠上的故障货物交接、强制机械取出。全部经真实入站处理器走完整的会话协议，
/// 再由真实引擎推进。最后一条是护栏：被交接的不是旅程阻塞在其上的那一条时，旅程照旧阻塞。
/// </para>
/// </remarks>
public sealed class RecoveryEndingReleasesBlockedJourneyTests
{
    private const string ProofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_499";
    private const string Proof = "recovery-ending-499-proof-not-a-production-secret";
    private const string RequestId = "44990000-0000-4000-8000-000000000001";
    private const string EventId = "34990000-0000-4000-8000-000000000001";
    private const string ActionId = "54990000-0000-4000-8000-000000000001";
    private const string ThirdSubmissionId = "24990000-0000-4000-8000-000000000021";
    private static readonly string[] HardwareChecks = ["LIVE_SLOT_SIGNALS_VALID"];
    private static readonly string[] HardwareActions = ["ADMINISTRATOR_CONFIRMED_HARDWARE_REPAIRED"];
    private static readonly string[] HardwareObservations = ["Lock replaced; doors shut and read locked."];

    /// <summary>
    /// 补偿（cs#291 实现者的探针形状）：一个取货站两条，第一条已装上，第二条装货结果要恢复、旅程阻塞；管理员补偿第二条、车证明仓位全空。
    /// 本站没有待装的了，车带着第一条离站。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationOfOneOfTwoDemandsAtOnePickupSendsTheVehicleOnWithTheOther()
    {
        await WithProofAsync(async () =>
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            await LoadTheFirstAndBlockOnTheSecondAtOnePickupAsync(fixture);
            int checksBefore = await CountAsync(fixture, "PreDepartureSafetyCheck");

            await CompensateAsync(fixture, SecondDemandId);
            await AssertReleasedAsync(fixture, JourneyRuntimeStage.AwaitingLoadResult);
            await fixture.RestoreSessionReadyAsync();
            Exception? first = await RunRoundAsync(fixture);
            Exception? second = await RunRoundAsync(fixture);

            await AssertAskedToLeaveAsync(fixture, checksBefore, first, second);
            Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, FirstDemandId)).Status);
        });
    }

    /// <summary>
    /// 补偿，在第二个取货站：第一站已装上第一条；第二站两条，在装的第二条要恢复、补偿掉，第三条还没装。回到等录入、发出录入请求，
    /// 第三条照常录入、下装货命令——与 cs#291 的装货途中取消同一条规则（A）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationAtASecondPickupWithAnotherDemandStillToLoadGoesBackToTheEntry()
    {
        await WithProofAsync(async () =>
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            await ArriveAtTheSecondPickupAsync(fixture, thirdAtTheSecondPickup: true);
            await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
            await BlockOnLoadAsync(fixture, SecondDemandId);

            await CompensateAsync(fixture, SecondDemandId);
            await fixture.RestoreSessionReadyAsync();
            Exception? round = await RunRoundAsync(fixture);

            JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
            Assert.True(
                after.Stage == JourneyRuntimeStage.AwaitingSublot,
                $"The stop still has a demand to load, so the journey should be back at the entry. " +
                $"stage={after.Stage} block={after.BlockReasonCode} round={round?.Message}");
            Assert.Null(after.BlockReasonCode);
            await EnterSublotAsync(fixture, FirstDemandId, ThirdSublot, ThirdSubmissionId);
            Assert.Equal(JourneyDemandStatuses.Loading, (await MembershipAsync(fixture, ThirdDemandId)).Status);
            Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, FirstDemandId)).Status);
        });
    }

    /// <summary>
    /// 交接，在卸货停靠上：一站两条，先卸的那一条卸货结果要恢复、旅程阻塞；管理员故障货物交接、车证明仓位全空。本站还有一条没卸：
    /// 发它的卸货命令，卸完旅程收尾。修之前没有任何路径会发这条命令——卸货侧的续跑要求本站已有一条 <c>UNLOADED</c>。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AHandoffOfOneOfTwoDemandsAtOneUnloadStopStillUnloadsTheOther()
    {
        await WithProofAsync(async () =>
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            await LoadBothAndArriveAtTheUnloadStopAsync(fixture);
            (string first, string second) = await UnloadOrderAsync(fixture);
            await BlockOnUnloadAsync(fixture, first);

            await HandOffAsync(fixture, first, SlotOperationType.Unload);
            await AssertReleasedAsync(fixture, JourneyRuntimeStage.AwaitingUnloadResult);
            await fixture.RestoreSessionReadyAsync();
            Exception? round = await RunRoundAsync(fixture);

            Assert.True(
                await UnloadCommandedAsync(fixture, second),
                $"The other demand at this stop was never commanded to unload. " +
                $"stage={(await JourneyOfAsync(fixture, second)).Stage} block={(await JourneyOfAsync(fixture, second)).BlockReasonCode} " +
                $"round={round?.Message}");
            await ApplySafeResultAsync(fixture, second, SlotOperationType.Unload, SlotBusinessState.Empty);
            await TickAndRunAsync(fixture);
            Assert.Equal(JourneyRuntimeStage.Completed, (await JourneyOfAsync(fixture, second)).Stage);
            Assert.Equal(JourneyDemandStatuses.Unloaded, (await MembershipAsync(fixture, second)).Status);
        });
    }

    /// <summary>
    /// 交接，在卸货停靠上、本站只有被交接的那一条、后面还有一站：两条需求各卸各的站，第一条在第一个卸货站卸货结果要恢复，交接掉。
    /// 车带着第二条离站去下一站。修之前放到等卸货结果会每轮抛 <c>nothing left to unload</c>（cs#291 审查）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AHandoffOfTheOnlyDemandOfAnUnloadStopWithAStopAfterItLeavesForThatStop()
    {
        await WithProofAsync(async () =>
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            await ArriveAtTheSecondPickupAsync(fixture, secondUnloadsAtItsOwnStop: true);
            await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
            await SettleLoadAsync(fixture, SecondDemandId);
            await AnswerDepartureSafetyAsync(fixture, FirstDemandId, SecondSafetyResultId);
            await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_GATE");
            Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
            Assert.True(await UnloadCommandedAsync(fixture, FirstDemandId), "The first unload stop is the first demand's.");
            await BlockOnUnloadAsync(fixture, FirstDemandId);
            int checksBefore = await CountAsync(fixture, "PreDepartureSafetyCheck");

            await HandOffAsync(fixture, FirstDemandId, SlotOperationType.Unload);
            await fixture.RestoreSessionReadyAsync();
            Exception? first = await RunRoundAsync(fixture);
            Exception? second = await RunRoundAsync(fixture);

            await AssertAskedToLeaveAsync(fixture, checksBefore, first, second);
            Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, SecondDemandId)).Status);
        });
    }

    /// <summary>
    /// 强制机械取出（REQ-0241/0242），取货停靠上一站两条：第一条已装上，第二条装货结果要恢复，强制取出后具名交接、终结第二条。强制取出
    /// 不证明仓门安全与车辆恢复，所以维修记录（<c>HardwareRecoveryRecord</c>）之前车一步不走、不发离站核验；之后带着第一条离站。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AForcedRecoveryOfOneOfTwoDemandsAtOnePickupLeavesOnlyAfterTheHardwareRecord()
    {
        await WithProofAsync(async () =>
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            await LoadTheFirstAndBlockOnTheSecondAtOnePickupAsync(fixture);
            int checksBefore = await CountAsync(fixture, "PreDepartureSafetyCheck");

            await using ControlServerDbContext connection = fixture.OpenConnectionContext();
            OnboardMessageProcessor processor = RecoveryProcessor(fixture, connection);
            OnboardConnectionState state = RecoveryConnection(fixture);
            int[] slots = await SlotsOfAsync(fixture, SecondDemandId, SlotOperationType.Load);
            string sessionId = await OpenSessionAsync(fixture, processor, state, SecondDemandId, slots);
            await processor.ProcessAsync(Action(fixture, sessionId, "FORCED_MECHANICAL_RECOVERY", SecondDemandId, slots), state, token);
            Assert.Equal("DurableAck", FirstLineType(await processor.ProcessAsync(Envelope(fixture, "ForcedMechanicalRecoveryResult", new
            {
                exceptionRecoverySessionId = sessionId,
                recoveryActionId = ActionId,
                forcedRecoveryGeneration = 1,
                outcome = "MECHANICALLY_ISOLATED",
                slots,
                @operator = BeforeSublotOperator(fixture),
                observedAt = fixture.Clock.GetUtcNow(),
                electronicEmptyProven = false,
                vehicleReadyProven = false
            }), state, token)));
            fixture.Context.ChangeTracker.Clear();
            Assert.Equal(JourneyDemandStatuses.Terminated, (await MembershipAsync(fixture, SecondDemandId)).Status);

            // 车以新的强制代次重连、报告没有未结的操作：仍因缺维修记录而不就绪。
            WireToGateStore store = new(connection);
            SessionRecoveryRow session = await connection.SessionRecoveries.AsNoTracking().SingleAsync(token);
            await store.ApplyRecoveryReportAsync(
                fixture.Options.AgvId, session.SessionGeneration, "f4990000-0000-4000-8000-000000000001",
                forcedRecoveryGeneration: 1, null, "NONE", [], [], [], token);
            Assert.Equal(WireToGateStore.ForcedRecoveryHardwareRecoveryRequired,
                (await store.DecideReadinessAsync(fixture.Options.AgvId, session.SessionGeneration, token)).ReasonCode);
            await RunRoundAsync(fixture);
            await RunRoundAsync(fixture);
            Assert.Equal(checksBefore, await CountAsync(fixture, "PreDepartureSafetyCheck"));

            string recorded = await processor.ProcessAsync(Envelope(fixture, "HardwareRecoveryRecordSubmitted", new
            {
                recordId = "e4990000-0000-4000-8000-000000000001",
                exceptionRecoverySessionId = sessionId,
                recoveryActionId = ActionId,
                @operator = BeforeSublotOperator(fixture),
                administratorRole = "MAINTENANCE_ADMINISTRATOR",
                slots,
                checksPerformed = HardwareChecks,
                actionsPerformed = HardwareActions,
                observations = HardwareObservations,
                observedAt = fixture.Clock.GetUtcNow()
            }), state, token);
            Assert.Equal("RECORDED", FirstLinePayload(recorded).GetProperty("outcome").GetString());
            fixture.Context.ChangeTracker.Clear();
            Assert.Equal(SessionReadiness.Ready, (await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(token)).Readiness);
            Exception? first = await RunRoundAsync(fixture);
            Exception? second = await RunRoundAsync(fixture);

            await AssertAskedToLeaveAsync(fixture, checksBefore, first, second);
            Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, FirstDemandId)).Status);
        });
    }

    /// <summary>
    /// 护栏（准入线第 1 条）：旅程阻塞在第二条的装货恢复上，管理员却把已经装上的第一条交接掉了。第二条的仓位操作仍未收敛，旅程不放出，
    /// 阻塞码照旧——终结的不是阻塞旅程的那一次操作，就不是放行的理由。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AHandoffOfAnotherDemandLeavesTheJourneyBlockedOnTheOperationStillUnresolved()
    {
        await WithProofAsync(async () =>
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            await LoadTheFirstAndBlockOnTheSecondAtOnePickupAsync(fixture);

            await HandOffAsync(fixture, FirstDemandId, SlotOperationType.Load);
            await fixture.RestoreSessionReadyAsync();
            await RunRoundAsync(fixture);

            JourneyRuntimeRow after = await JourneyOfAsync(fixture, SecondDemandId);
            Assert.Equal(JourneyRuntimeStage.Blocked, after.Stage);
            Assert.Equal("LOAD_RESULT_REQUIRES_RECOVERY", after.BlockReasonCode);
            Assert.Equal(StationOperationStatus.RecoveryRequired, (await OperationOfAsync(fixture, SecondDemandId, SlotOperationType.Load)).Status);
        });
    }

    /// <summary>
    /// 护栏的第二个条件单独钉住：结清的正是旅程阻塞在其上的那一次，但这趟旅程另有一次仓位操作没收敛，不放。正常流程里同一站逐条串行，
    /// 到不了这个状态，所以改库造出它（第一条已装上的那次装货改成要恢复）——它守的是服务器自己的不变量被破坏时不放车。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationLeavesTheJourneyBlockedWhileAnotherOperationOfItIsUnresolved()
    {
        await WithProofAsync(async () =>
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            await LoadTheFirstAndBlockOnTheSecondAtOnePickupAsync(fixture);
            await using (ControlServerDbContext context = fixture.OpenConnectionContext())
            {
                (await context.StationOperations.SingleAsync(
                    row => row.DemandId == FirstDemandId && row.OperationType == SlotOperationType.Load, token)).Status =
                    StationOperationStatus.RecoveryRequired;
                await context.SaveChangesAsync(token);
            }
            fixture.Context.ChangeTracker.Clear();

            await CompensateAsync(fixture, SecondDemandId);

            JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
            Assert.Equal((JourneyRuntimeStage.Blocked, "LOAD_RESULT_REQUIRES_RECOVERY"), (after.Stage, after.BlockReasonCode));
        });
    }

    /// <summary>
    /// 护栏的第一个条件单独钉住：旅程阻塞的原因不是哪一次仓位操作（这里是一条没对上的取消结果留下的阻塞），会话把本站已装上的那一条
    /// 交接掉。结清的那一次装货早已提交，不是它挡着旅程，所以不放，原来的阻塞码照旧。阻塞码是改库写的，形状与
    /// <c>KeepDemandAndJourneyBlockedAsync</c> 写的相同。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AHandoffDoesNotLiftABlockItsOperationDidNotCause()
    {
        await WithProofAsync(async () =>
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            JourneyRuntimeRow runtime = await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
            await AddInboxAsync(
                fixture, FirstSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, FirstSublot));
            await TickAndRunAsync(fixture);
            await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
            await TickAndRunAsync(fixture);
            Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, FirstDemandId)).Status);
            const string otherBlock = "LoadCancellationResult_NOT_RECONCILED";
            await using (ControlServerDbContext context = fixture.OpenConnectionContext())
            {
                JourneyRuntimeRow row = await context.JourneyRuntimes.SingleAsync(item => item.JourneyId == runtime.JourneyId, token);
                row.Stage = JourneyRuntimeStage.Blocked;
                row.SetBlockReason(otherBlock, fixture.Clock.GetUtcNow());
                await context.SaveChangesAsync(token);
            }
            fixture.Context.ChangeTracker.Clear();

            await HandOffAsync(fixture, FirstDemandId, SlotOperationType.Load);

            JourneyRuntimeRow after = await JourneyOfAsync(fixture, SecondDemandId);
            Assert.Equal((JourneyRuntimeStage.Blocked, otherBlock), (after.Stage, after.BlockReasonCode));
        });
    }

    /// <summary>一个取货站两条：第一条装上，第二条录入、下命令，结果超时进恢复，旅程阻塞在它上面。</summary>
    private static async Task LoadTheFirstAndBlockOnTheSecondAtOnePickupAsync(RuntimeFixture fixture)
    {
        JourneyRuntimeRow runtime = await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
        await AddInboxAsync(
            fixture, FirstSubmissionId, "SublotSubmitted", await SublotSubmissionAsync(fixture, runtime, FirstSublot));
        await TickAndRunAsync(fixture);
        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);
        await EnterSublotAsync(fixture, FirstDemandId, SecondSublot, SecondSubmissionId);
        Assert.Equal(JourneyDemandStatuses.Loading, (await MembershipAsync(fixture, SecondDemandId)).Status);
        await BlockOnLoadAsync(fixture, SecondDemandId);
    }

    private static async Task BlockOnLoadAsync(RuntimeFixture fixture, string demandId)
    {
        await fixture.ApplyTimedOutResultAsync(await OperationOfAsync(fixture, demandId, SlotOperationType.Load), SlotOperationType.Load);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow blocked = await JourneyOfAsync(fixture, demandId);
        Assert.Equal((JourneyRuntimeStage.Blocked, "LOAD_RESULT_REQUIRES_RECOVERY"), (blocked.Stage, blocked.BlockReasonCode));
    }

    private static async Task BlockOnUnloadAsync(RuntimeFixture fixture, string demandId)
    {
        await fixture.ApplyTimedOutResultAsync(await OperationOfAsync(fixture, demandId, SlotOperationType.Unload), SlotOperationType.Unload);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow blocked = await JourneyOfAsync(fixture, demandId);
        Assert.Equal((JourneyRuntimeStage.Blocked, "UNLOAD_RESULT_REQUIRES_RECOVERY"), (blocked.Stage, blocked.BlockReasonCode));
    }

    /// <summary>完整的装货补偿：开会话、选 <c>COMPENSATE_LOAD_ALL_EMPTY</c>、车请求授权、车报仓位全空。</summary>
    private static async Task CompensateAsync(RuntimeFixture fixture, string demandId)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        StationOperationRow load = await OperationOfAsync(fixture, demandId, SlotOperationType.Load);
        int[] slots = JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!;
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            OnboardMessageProcessor processor = RecoveryProcessor(fixture, connection);
            OnboardConnectionState state = RecoveryConnection(fixture);
            string sessionId = await OpenSessionAsync(fixture, processor, state, demandId, slots);
            await processor.ProcessAsync(Action(fixture, sessionId, "COMPENSATE_LOAD_ALL_EMPTY", demandId, slots), state, token);
            await processor.ProcessAsync(Envelope(fixture, "LoadCompensationRequested", new
            {
                recoveryActionId = ActionId,
                exceptionRecoverySessionId = sessionId,
                demandId,
                slotOperationAttemptId = load.SlotOperationAttemptId,
                @operator = BeforeSublotOperator(fixture)
            }), state, token);
            string ack = await processor.ProcessAsync(Envelope(fixture, "LoadCompensationResult", new
            {
                recoveryActionId = ActionId,
                demandId,
                slotOperationAttemptId = load.SlotOperationAttemptId,
                overallOutcome = "ALL_EMPTY",
                slotResults = EmptySlots(slots),
                observedAt = fixture.Clock.GetUtcNow()
            }), state, token);
            Assert.Equal("DurableAck", FirstLineType(ack));
        }
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(JourneyDemandStatuses.Terminated, (await MembershipAsync(fixture, demandId)).Status);
    }

    /// <summary>完整的故障货物交接：开会话、选 <c>FAULT_CARGO_HANDOFF</c>、车报已交接且仓位全空。</summary>
    private static async Task HandOffAsync(RuntimeFixture fixture, string demandId, SlotOperationType latest)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        int[] slots = await SlotsOfAsync(fixture, demandId, latest);
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            OnboardMessageProcessor processor = RecoveryProcessor(fixture, connection);
            OnboardConnectionState state = RecoveryConnection(fixture);
            string sessionId = await OpenSessionAsync(fixture, processor, state, demandId, slots);
            await processor.ProcessAsync(Action(fixture, sessionId, "FAULT_CARGO_HANDOFF", demandId, slots), state, token);
            string handoffId = (await connection.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowType == "FAULT_CARGO_HANDOFF", token)).HandoffId!;
            string ack = await processor.ProcessAsync(Envelope(fixture, "FaultCargoRecoveryResult", new
            {
                exceptionRecoverySessionId = sessionId,
                recoveryActionId = ActionId,
                demandId,
                handoffId,
                overallOutcome = "HANDED_OFF",
                slotResults = EmptySlots(slots),
                @operator = BeforeSublotOperator(fixture),
                observedAt = fixture.Clock.GetUtcNow()
            }), state, token);
            Assert.Equal("DurableAck", FirstLineType(ack));
        }
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(JourneyDemandStatuses.Terminated, (await MembershipAsync(fixture, demandId)).Status);
    }

    private static object[] EmptySlots(int[] slots) =>
    [
        .. slots.Select(slot => new
        {
            slotNo = slot,
            outcome = "COMPLETED",
            finalPhysicalState = "EMPTY",
            lockState = "LOCKED",
            unlockOutputState = "RESET",
            reasonCodes = Array.Empty<string>()
        })
    ];

    private static async Task<string> OpenSessionAsync(
        RuntimeFixture fixture,
        OnboardMessageProcessor processor,
        OnboardConnectionState state,
        string demandId,
        int[] slots)
    {
        string opened = await processor.ProcessAsync(
            Envelope(fixture, "ExceptionRecoverySessionRequested", new
            {
                requestId = RequestId,
                administrator = BeforeSublotOperator(fixture),
                administratorRole = "MAINTENANCE_ADMINISTRATOR",
                eventId = EventId,
                demandId,
                slots,
                reason = "The operation cannot be recovered in place.",
                authenticationProof = Proof
            }),
            state,
            TestContext.Current.CancellationToken);
        Assert.Equal("ExceptionRecoverySessionOpened", FirstLineType(opened));
        return FirstLinePayload(opened).GetProperty("exceptionRecoverySessionId").GetString()!;
    }

    private static string Action(RuntimeFixture fixture, string sessionId, string action, string demandId, int[] slots) =>
        Envelope(fixture, "RecoveryActionSubmitted", new
        {
            recoveryActionId = ActionId,
            exceptionRecoverySessionId = sessionId,
            action,
            eventId = EventId,
            demandId,
            slots,
            @operator = BeforeSublotOperator(fixture),
            reason = "End this demand; the journey carries another."
        });

    /// <summary>
    /// 规则本身，在引擎跑之前读：恢复结果落库的那一次保存里，旅程已回到等那一次操作结果的阶段、阻塞码已清（与修复续行同一个做法）。
    /// 引擎下一轮会改写阻塞码，所以要在它之前读。
    /// </summary>
    private static async Task AssertReleasedAsync(RuntimeFixture fixture, JourneyRuntimeStage expected)
    {
        JourneyRuntimeRow released = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal((expected, (string?)null), (released.Stage, released.BlockReasonCode));
    }

    /// <summary>判据：发出了一条新的离站核验，旅程在等它的答复，阻塞码已清。</summary>
    private static async Task AssertAskedToLeaveAsync(
        RuntimeFixture fixture, int checksBefore, Exception? first, Exception? second)
    {
        JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(
            await CountAsync(fixture, "PreDepartureSafetyCheck") == checksBefore + 1 &&
            after.Stage == JourneyRuntimeStage.AwaitingDepartureSafety,
            $"The vehicle was never asked to leave with what it still carries. stage={after.Stage} block={after.BlockReasonCode} " +
            $"first={first?.Message} second={second?.Message}");
    }

    private static Task<int> CountAsync(RuntimeFixture fixture, string messageType) =>
        fixture.Context.ProtocolOutbox.AsNoTracking()
            .CountAsync(row => row.MessageType == messageType, TestContext.Current.CancellationToken);

    private static Task<StationOperationRow> OperationOfAsync(RuntimeFixture fixture, string demandId, SlotOperationType type) =>
        fixture.Context.StationOperations.AsNoTracking().SingleAsync(
            row => row.DemandId == demandId && row.OperationType == type, TestContext.Current.CancellationToken);

    private static async Task<int[]> SlotsOfAsync(RuntimeFixture fixture, string demandId, SlotOperationType type) =>
        JsonSerializer.Deserialize<int[]>((await OperationOfAsync(fixture, demandId, type)).TargetSlotsJson)!;

    private static OnboardMessageProcessor RecoveryProcessor(RuntimeFixture fixture, ControlServerDbContext connection) =>
        TestOnboardProcessorFactory.Create(
            connection,
            new WireToGateStore(connection),
            fixture.Clock,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Recovery:AuthenticationProofEnvironmentVariable"] = ProofVariable
                })
                .Build());

    private static OnboardConnectionState RecoveryConnection(RuntimeFixture fixture) => new()
    {
        AgvId = fixture.Options.AgvId,
        SessionGeneration = 1,
        CapabilityRevision = 1,
        SafetyRevision = 7,
        Readiness = SessionReadiness.RecoveryRequired
    };

    private static string Envelope(RuntimeFixture fixture, string messageType, object payload) =>
        BeforeSublotEnvelope(fixture, Guid.NewGuid().ToString("D"), messageType, generation: 1, payload);

    private static string FirstLineType(string wire)
    {
        string first = wire.Split('\n', StringSplitOptions.RemoveEmptyEntries).First();
        using JsonDocument document = JsonDocument.Parse(first);
        return document.RootElement.GetProperty("messageType").GetString()!;
    }

    private static async Task WithProofAsync(Func<Task> body)
    {
        Environment.SetEnvironmentVariable(ProofVariable, Proof);
        try
        {
            await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProofVariable, null);
        }
    }
}
