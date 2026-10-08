using System.Security.Cryptography;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.StopEndedJourneyContinuesTests;
using static ControlServer.Tests.UnloadCrashResumeTests;

namespace ControlServer.Tests;

/// <summary>
/// 另一条放行路径：修复续行（<c>RESUME_AFTER_REPAIR</c>）的新结果对上了，<c>OnboardRecoveryCoordinator.ObserveOperationResultAsync</c>
/// 把旅程从 <c>Blocked</c> 放回等那一次结果的阶段（control-server#506）。
/// </summary>
/// <remarks>
/// 修之前它不看旅程里别的需求：一条需求的事实还没结论，车照样被放走（与 cs#499 独立审查 M-1 同一类，碰准入线第 1 条）。
/// 全部经真实入站处理器走完整的会话协议（开会话、选修复续行、车报新结果），再由真实引擎推进。
/// </remarks>
public sealed partial class RecoveryEndingReleasesBlockedJourneyTests
{
    private static readonly JsonSerializerOptions ResultSerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 对照：旅程里只有第二条的装货待恢复，修复续行对上了，旅程放出，车带着两条离站。这是修复续行本来的样子，修之后不变。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task ARepairResumeOfTheOnlyUnresolvedDemandSendsTheVehicleOn()
    {
        await WithProofAsync(async () =>
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            await LoadTheFirstAndBlockOnTheSecondAtOnePickupAsync(fixture);
            int checksBefore = await CountAsync(fixture, "PreDepartureSafetyCheck");

            await ResumeLoadAsync(fixture, SecondDemandId);
            await AssertReleasedAsync(fixture, JourneyRuntimeStage.AwaitingLoadResult);
            await fixture.RestoreSessionReadyAsync();
            Exception? first = await RunRoundAsync(fixture);
            Exception? second = await RunRoundAsync(fixture);

            await AssertAskedToLeaveAsync(fixture, checksBefore, first, second);
            Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, SecondDemandId)).Status);
        });
    }

    /// <summary>
    /// 票面的链条（control-server#506）：旅程阻塞在第二条的装货恢复上；管理员先交接第一条，车报已交接、一个仓位却读到 <c>OCCUPIED</c>，
    /// 结果没对上，第一条留在 <c>RecoveryRequired</c>。接着对第二条修复续行，新结果对上了。第一条的货在不在车上没有结论，旅程不放出、
    /// 不发离站核验。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task ARepairResumeLeavesTheJourneyBlockedWhileAHandoffLeftAnotherDemandAwaitingRecovery()
    {
        await WithProofAsync(async () =>
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            await LoadTheFirstAndBlockOnTheSecondAtOnePickupAsync(fixture);
            int[] firstSlots = await SlotsOfAsync(fixture, FirstDemandId, SlotOperationType.Load);
            await HandOffAsync(
                fixture, FirstDemandId, SlotOperationType.Load, FirstHandoffRequestId, FirstHandoffActionId, occupiedSlot: firstSlots[0]);
            Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await DemandOfAsync(fixture, FirstDemandId)).Status);
            int checksBefore = await CountAsync(fixture, "PreDepartureSafetyCheck");

            await ResumeLoadAsync(fixture, SecondDemandId);
            JourneyRuntimeRow atResult = await JourneyOfAsync(fixture, FirstDemandId);
            await fixture.RestoreSessionReadyAsync();
            Exception? round = await RunRoundAsync(fixture);
            await RunRoundAsync(fixture);

            JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
            int checks = await CountAsync(fixture, "PreDepartureSafetyCheck") - checksBefore;
            Assert.True(
                atResult.Stage == JourneyRuntimeStage.Blocked && after.Stage == JourneyRuntimeStage.Blocked && checks == 0,
                $"A repair resume released the journey while the first demand still awaits recovery. " +
                $"at-result={atResult.Stage}/{atResult.BlockReasonCode} after-rounds={after.Stage}/{after.BlockReasonCode} " +
                $"departure-checks={checks} round={round?.Message}");
        });
    }

    /// <summary>
    /// 第 4 条单独钉住：阻塞码是普通的 <c>LOAD_RESULT_REQUIRES_RECOVERY</c>（第 5 条挡不住），第一条已装上的需求被标成待恢复。上面票面那一格的
    /// 阻塞码是 <c>FaultCargoRecoveryResult_NOT_RECONCILED</c>，第 4、5 条都挡它，去掉任何一条它都不红，所以这里改库造出只有第 4 条挡得住的状态
    /// （修之前它的来路正是本票的放错：放出时不看别的需求，之后旅程又在另一次装货上阻塞）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task ARepairResumeUnderAnOrdinaryBlockLeavesTheJourneyBlockedWhileAnotherDemandAwaitsRecovery()
    {
        await WithProofAsync(async () =>
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            await LoadTheFirstAndBlockOnTheSecondAtOnePickupAsync(fixture);
            await using (ControlServerDbContext context = fixture.OpenConnectionContext())
            {
                (await context.AcceptedDemands.SingleAsync(row => row.DemandId == FirstDemandId, token)).Status =
                    DemandExecutionStatus.RecoveryRequired;
                await context.SaveChangesAsync(token);
            }
            fixture.Context.ChangeTracker.Clear();

            await ResumeLoadAsync(fixture, SecondDemandId);

            JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
            Assert.Equal((JourneyRuntimeStage.Blocked, "LOAD_RESULT_REQUIRES_RECOVERY"), (after.Stage, after.BlockReasonCode));
        });
    }

    /// <summary>
    /// 第 3 条单独钉住：旅程里另一次仓位操作（第一条已装上的那次装货）没收敛，而它的需求没被标成待恢复，阻塞码也普通。正常流程里同一站逐条串行，
    /// 到不了这个状态，改库造出——与 <see cref="ACompensationLeavesTheJourneyBlockedWhileAnotherOperationOfItIsUnresolved"/> 同一个做法，
    /// 守的是服务器自己的不变量被破坏时不放车。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task ARepairResumeLeavesTheJourneyBlockedWhileAnotherOperationOfItIsUnresolved()
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

            await ResumeLoadAsync(fixture, SecondDemandId);

            JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
            Assert.Equal((JourneyRuntimeStage.Blocked, "LOAD_RESULT_REQUIRES_RECOVERY"), (after.Stage, after.BlockReasonCode));
        });
    }


    /// <summary>
    /// 同一趟旅程两次修复续行（独立审查 M-1，审查探针的形状）：取货站上第二条的装货修复续行对上，旅程放出、车带着两条到卸货站；先卸的
    /// 第一条卸货结果要恢复，再修复续行也对上了。旅程放回等卸货结果。修之前第二条在第一次修复续行之后一直留着 <c>RecoveryRequired</c>
    /// （<c>ApplyOperationResultAsync</c> 只把装货改成 <c>Committed</c>，不动需求），第二次修复续行时第 4 条把它当成「别的需求待恢复」，
    /// 旅程卡在 <c>Blocked</c>；不改库的出口只剩把装得好好的第二条交接掉。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task ASecondRepairResumeInOneJourneyIsNotHeldByTheFirstOnesDemand()
    {
        await WithProofAsync(async () =>
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            string first = await ResumeTheSecondLoadAndBlockOnTheFirstUnloadAsync(fixture);

            await ResumeAsync(fixture, first, SlotOperationType.Unload, SecondResume);

            JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
            Assert.True(
                after.Stage == JourneyRuntimeStage.AwaitingUnloadResult && after.BlockReasonCode is null,
                $"The second repair resume was held by the demand the first one recovered. stage={after.Stage} " +
                $"block={after.BlockReasonCode} second-demand={(await DemandOfAsync(fixture, SecondDemandId)).Status}");
        });
    }

    /// <summary>
    /// 同一个根因落在 cs#499 的终结放行上：形状同上，第二次改成故障货物交接先卸的那一条。修之前同样卡在 <c>Blocked</c>
    /// （修之前的基线上也是，与本票的新判据无关）：第 4 条读到第一次修复续行留下的过时标记。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AHandoffAfterAnEarlierRepairResumeInTheJourneyIsNotHeldByItsDemand()
    {
        await WithProofAsync(async () =>
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            string first = await ResumeTheSecondLoadAndBlockOnTheFirstUnloadAsync(fixture);

            await HandOffAsync(fixture, first, SlotOperationType.Unload);

            JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
            Assert.True(
                after.Stage == JourneyRuntimeStage.AwaitingUnloadResult && after.BlockReasonCode is null,
                $"A handoff was held by the demand an earlier repair resume recovered. stage={after.Stage} " +
                $"block={after.BlockReasonCode} second-demand={(await DemandOfAsync(fixture, SecondDemandId)).Status}");
        });
    }

    /// <summary>
    /// cs#506 留下的卡死，control-server#505 放开：单需求旅程，装货结果要恢复；先补偿，车报 <c>UNKNOWN</c>（仓位状态读不出），没对上，
    /// 阻塞码变成 <c>LoadCompensationResult_NOT_RECONCILED</c>，需求留在 <c>RecoveryRequired</c>，装货操作仍是 <c>RecoveryRequired</c>。
    /// 车的恢复报告仍报这一次操作没结清、停在 <c>ACTIVE_UNLOCK_SET</c>，再开会话修复续行，新结果对上了。这一串是现场形状（cs#506 独立审查读两端
    /// 代码推断可达）。cs#506 时第 5 条对 <c>*_NOT_RECONCILED</c> 一律不放，旅程留在 <c>Blocked</c>、只能改库；#505 之后普通的
    /// <c>*_NOT_RECONCILED</c> 必有一条待恢复的需求作标记，修复续行把那条需求放回 <c>Accepted</c>，旅程里再没有标记，放出。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task ARepairResumeAfterAnUnreconciledCompensationOfTheSameDemandSendsTheVehicleOn()
    {
        await WithProofAsync(async () =>
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
            fixture.BoxCounts.Set(FirstSublot, 4);
            await TickAndRunAsync(fixture);
            JourneyRuntimeRow runtime = await fixture.RuntimeAsync(FirstDemandId);
            fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupUpperId, runtime.PickupStationRiotId);
            fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
            await TickAndRunAsync(fixture);
            await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
            await BlockOnLoadAsync(fixture, FirstDemandId);

            StationOperationRow load = await OperationOfAsync(fixture, FirstDemandId, SlotOperationType.Load);
            int[] slots = JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!;
            await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
            {
                OnboardMessageProcessor processor = RecoveryProcessor(fixture, connection);
                OnboardConnectionState state = RecoveryConnection(fixture);
                string sessionId = await OpenSessionAsync(fixture, processor, state, FirstDemandId, slots);
                await processor.ProcessAsync(
                    Action(fixture, sessionId, "COMPENSATE_LOAD_ALL_EMPTY", FirstDemandId, slots), state, token);
                await processor.ProcessAsync(Envelope(fixture, "LoadCompensationRequested", new
                {
                    recoveryActionId = ActionId,
                    exceptionRecoverySessionId = sessionId,
                    demandId = FirstDemandId,
                    slotOperationAttemptId = load.SlotOperationAttemptId,
                    @operator = BeforeSublotOperator(fixture)
                }), state, token);
                Assert.Equal("DurableAck", FirstLineType(await processor.ProcessAsync(Envelope(fixture, "LoadCompensationResult", new
                {
                    recoveryActionId = ActionId,
                    demandId = FirstDemandId,
                    slotOperationAttemptId = load.SlotOperationAttemptId,
                    overallOutcome = "UNKNOWN",
                    slotResults = slots.Select(slot => new
                    {
                        slotNo = slot,
                        outcome = "UNKNOWN",
                        finalPhysicalState = "UNKNOWN",
                        lockState = "UNKNOWN",
                        unlockOutputState = "UNKNOWN",
                        reasonCodes = UnknownReasonCodes
                    }).ToArray(),
                    observedAt = fixture.Clock.GetUtcNow()
                }), state, token)));
            }
            fixture.Context.ChangeTracker.Clear();
            const string unreconciled = "LoadCompensationResult_NOT_RECONCILED";
            JourneyRuntimeRow blocked = await JourneyOfAsync(fixture, FirstDemandId);
            Assert.Equal((JourneyRuntimeStage.Blocked, unreconciled), (blocked.Stage, blocked.BlockReasonCode));
            Assert.Equal(StationOperationStatus.RecoveryRequired, (await OperationOfAsync(fixture, FirstDemandId, SlotOperationType.Load)).Status);

            Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await DemandOfAsync(fixture, FirstDemandId)).Status);

            await ResumeAsync(fixture, FirstDemandId, SlotOperationType.Load, FirstResume with { Checkpoint = "ACTIVE_UNLOCK_SET" });

            await AssertReleasedAsync(fixture, JourneyRuntimeStage.AwaitingLoadResult);
            int checksBefore = await CountAsync(fixture, "PreDepartureSafetyCheck");
            await fixture.RestoreSessionReadyAsync();
            Exception? first = await RunRoundAsync(fixture);
            Exception? second = await RunRoundAsync(fixture);
            await AssertAskedToLeaveAsync(fixture, checksBefore, first, second);
        });
    }

    /// <summary>一次修复续行会话用到的标识与车报的检查点；同一个用例里两次修复续行要用两套。</summary>
    private sealed record ResumeIds(string RequestId, string ActionId, string ReportId, string Checkpoint = "PREPARED");

    private static readonly ResumeIds FirstResume = new(
        "45060000-0000-4000-8000-000000000001", "55060000-0000-4000-8000-000000000001", "f5060000-0000-4000-8000-000000000001");

    private static readonly ResumeIds SecondResume = new(
        "45060000-0000-4000-8000-000000000002", "55060000-0000-4000-8000-000000000002", "f5060000-0000-4000-8000-000000000002");

    private static Task ResumeLoadAsync(RuntimeFixture fixture, string demandId) =>
        ResumeAsync(fixture, demandId, SlotOperationType.Load, FirstResume);

    /// <summary>
    /// 一个取货站两条：第二条装货结果要恢复、修复续行对上，旅程放出；车答离站核验、到卸货站，先卸的那一条卸货结果要恢复、旅程阻塞。
    /// 返回先卸的那一条。
    /// </summary>
    private static async Task<string> ResumeTheSecondLoadAndBlockOnTheFirstUnloadAsync(RuntimeFixture fixture)
    {
        await LoadTheFirstAndBlockOnTheSecondAtOnePickupAsync(fixture);
        await ResumeLoadAsync(fixture, SecondDemandId);
        await fixture.RestoreSessionReadyAsync();
        await RunRoundAsync(fixture);
        await RunRoundAsync(fixture);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_GATE");
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        (string first, _) = await UnloadOrderAsync(fixture);
        await BlockOnUnloadAsync(fixture, first);
        return first;
    }

    /// <summary>
    /// 修复续行所在的会话：开会话、选 <c>RESUME_AFTER_REPAIR</c>、车按它重做那一次装卸并报一条对上的新结果。车先在恢复报告里报出这一次
    /// 操作没结清、停在已证明的检查点——授权修复续行要求这两样。
    /// </summary>
    private static async Task ResumeAsync(RuntimeFixture fixture, string demandId, SlotOperationType type, ResumeIds ids)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        StationOperationRow operation = await OperationOfAsync(fixture, demandId, type);
        int[] slots = JsonSerializer.Deserialize<int[]>(operation.TargetSlotsJson)!;
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            SessionRecoveryRow session = await connection.SessionRecoveries.AsNoTracking().SingleAsync(token);
            await new WireToGateStore(connection).ApplyRecoveryReportAsync(
                fixture.Options.AgvId, session.SessionGeneration, ids.ReportId,
                forcedRecoveryGeneration: 0, operation.SlotOperationAttemptId, ids.Checkpoint, [], [], [], token);
            OnboardMessageProcessor processor = RecoveryProcessor(fixture, connection);
            OnboardConnectionState state = RecoveryConnection(fixture);
            string sessionId = await OpenSessionAsync(fixture, processor, state, demandId, slots, ids.RequestId);
            Assert.Equal("RecoveryActionAccepted", FirstLineType(await processor.ProcessAsync(
                Action(fixture, sessionId, "RESUME_AFTER_REPAIR", demandId, slots, ids.ActionId), state, token)));
            Assert.Equal("DurableAck", FirstLineType(await processor.ProcessAsync(
                Envelope(fixture, "OperationResult", ResumedResult(fixture, demandId, operation.SlotOperationAttemptId, slots, type)),
                state,
                token)));
        }
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(StationOperationStatus.Committed, (await OperationOfAsync(fixture, demandId, type)).Status);
    }

    /// <summary>
    /// 修复续行之后车报的新结果：每个仓位做完、锁闭、开锁输出复位，装货读到有货、卸货读到空。哈希按车端的做法从 CLR 值直接算。
    /// </summary>
    private static object ResumedResult(
        RuntimeFixture fixture, string demandId, string attemptId, int[] slots, SlotOperationType type)
    {
        var withoutHash = new
        {
            demandId,
            slotOperationAttemptId = attemptId,
            operationType = type == SlotOperationType.Load ? "LOAD" : "UNLOAD",
            overallOutcome = "COMPLETED",
            slotResults = slots.Select(slot => new
            {
                slotNo = slot,
                outcome = "COMPLETED",
                finalPhysicalState = type == SlotOperationType.Load ? "OCCUPIED" : "EMPTY",
                lockState = "LOCKED",
                unlockOutputState = "RESET",
                reasonCodes = Array.Empty<string>()
            }).ToArray(),
            observedAt = fixture.Clock.GetUtcNow(),
            journalCheckpoint = "RESUME_RESULT_RECORDED"
        };
        byte[] businessContent = JsonSerializer.SerializeToUtf8Bytes(withoutHash, ResultSerializerOptions);
        return new
        {
            withoutHash.demandId,
            withoutHash.slotOperationAttemptId,
            withoutHash.operationType,
            withoutHash.overallOutcome,
            withoutHash.slotResults,
            withoutHash.observedAt,
            withoutHash.journalCheckpoint,
            resultContentSha256 = Convert.ToHexString(SHA256.HashData(businessContent)).ToLowerInvariant()
        };
    }
}
