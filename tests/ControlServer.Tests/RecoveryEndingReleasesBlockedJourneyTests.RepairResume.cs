using System.Security.Cryptography;
using System.Text.Json;
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
    private const string ResumeRequestId = "45060000-0000-4000-8000-000000000001";
    private const string ResumeActionId = "55060000-0000-4000-8000-000000000001";
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
    /// 修复续行所在的会话：开会话、选 <c>RESUME_AFTER_REPAIR</c>、车按它重做那一次装货并报一条对上的新结果。车先在恢复报告里报出这一次
    /// 操作没结清、停在已证明的检查点（<c>PREPARED</c>）——授权修复续行要求这两样。
    /// </summary>
    private static async Task ResumeLoadAsync(RuntimeFixture fixture, string demandId)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        StationOperationRow load = await OperationOfAsync(fixture, demandId, SlotOperationType.Load);
        int[] slots = JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!;
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            SessionRecoveryRow session = await connection.SessionRecoveries.AsNoTracking().SingleAsync(token);
            await new WireToGateStore(connection).ApplyRecoveryReportAsync(
                fixture.Options.AgvId, session.SessionGeneration, "f5060000-0000-4000-8000-000000000001",
                forcedRecoveryGeneration: 0, load.SlotOperationAttemptId, "PREPARED", [], [], [], token);
            OnboardMessageProcessor processor = RecoveryProcessor(fixture, connection);
            OnboardConnectionState state = RecoveryConnection(fixture);
            string sessionId = await OpenSessionAsync(fixture, processor, state, demandId, slots, ResumeRequestId);
            Assert.Equal("RecoveryActionAccepted", FirstLineType(await processor.ProcessAsync(
                Action(fixture, sessionId, "RESUME_AFTER_REPAIR", demandId, slots, ResumeActionId), state, token)));
            Assert.Equal("DurableAck", FirstLineType(await processor.ProcessAsync(
                Envelope(fixture, "OperationResult", ResumedLoadResult(fixture, demandId, load.SlotOperationAttemptId, slots)),
                state,
                token)));
        }
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(StationOperationStatus.Committed, (await OperationOfAsync(fixture, demandId, SlotOperationType.Load)).Status);
    }

    /// <summary>修复续行之后车报的新装货结果：每个仓位装上、锁闭、开锁输出复位。哈希按车端的做法从 CLR 值直接算。</summary>
    private static object ResumedLoadResult(RuntimeFixture fixture, string demandId, string attemptId, int[] slots)
    {
        var withoutHash = new
        {
            demandId,
            slotOperationAttemptId = attemptId,
            operationType = "LOAD",
            overallOutcome = "COMPLETED",
            slotResults = slots.Select(slot => new
            {
                slotNo = slot,
                outcome = "COMPLETED",
                finalPhysicalState = "OCCUPIED",
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
