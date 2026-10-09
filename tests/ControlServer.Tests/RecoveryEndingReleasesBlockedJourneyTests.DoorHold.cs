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
/// 两条放行路径的交叉（control-server#532 合并时两侧各带一条）：门锁未证明的全空补偿结果（REQ-0364，CP-0009，cs#385）先写门锁扣车，
/// 随后同一次收尾里 <c>BlockedJourneyRelease</c>（cs#499）把阻塞在这次操作上的旅程放回等结果的阶段。放回只动旅程阶段：
/// 扣车让车保持 <c>RecoveryRequired</c> / <c>SLOT_DOOR_REPAIR_RELEASE_REQUIRED</c>，引擎不叫它离站。
/// </summary>
public sealed partial class RecoveryEndingReleasesBlockedJourneyTests
{
    private static readonly string[] DoorUnprovenReasonCodes = ["SLOT_DOOR_LOCK_UNPROVEN_AFTER_EMPTY"];

    /// <summary>
    /// 一个取货站两条需求：第一条已装上，第二条装货阻塞；第二条以门锁未证明的全空结果补偿。旅程放回，扣车行写下且未解除；
    /// 再补一份什么都没挂起的恢复报告，就绪仍被扣车挡住；引擎跑两轮，阶段不变、不发离站核验。对照：就绪恢复后同样两轮会推进，
    /// 所以上面的「不动」是看得见的（审查 #534 的 S1，探针并入；杀变异 M2：放回成功时删掉本次刚写下、未保存的扣车行）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-LOAD-COMPENSATION-EMPTY-DOOR-UNPROVEN")]
    public async Task ADoorUnprovenCompensationReleasesTheBlockedJourneyButTheDoorHoldKeepsTheVehicle()
    {
        await WithProofAsync(async () =>
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            await LoadTheFirstAndBlockOnTheSecondAtOnePickupAsync(fixture);
            int checksBefore = await CountAsync(fixture, "PreDepartureSafetyCheck");

            StationOperationRow load = await OperationOfAsync(fixture, SecondDemandId, SlotOperationType.Load);
            int[] slots = JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!;
            await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
            {
                OnboardMessageProcessor processor = RecoveryProcessor(fixture, connection);
                OnboardConnectionState state = RecoveryConnection(fixture);
                string sessionId = await OpenSessionAsync(fixture, processor, state, SecondDemandId, slots);
                await processor.ProcessAsync(
                    Action(fixture, sessionId, "COMPENSATE_LOAD_ALL_EMPTY", SecondDemandId, slots), state, token);
                await processor.ProcessAsync(Envelope(fixture, "LoadCompensationRequested", new
                {
                    recoveryActionId = ActionId,
                    exceptionRecoverySessionId = sessionId,
                    demandId = SecondDemandId,
                    slotOperationAttemptId = load.SlotOperationAttemptId,
                    @operator = BeforeSublotOperator(fixture)
                }), state, token);
                string ack = await processor.ProcessAsync(Envelope(fixture, "LoadCompensationResult", new
                {
                    recoveryActionId = ActionId,
                    demandId = SecondDemandId,
                    slotOperationAttemptId = load.SlotOperationAttemptId,
                    overallOutcome = "ALL_EMPTY_DOOR_UNPROVEN",
                    slotResults = slots.Select(slot => new
                    {
                        slotNo = slot,
                        outcome = "FAILED",
                        finalPhysicalState = "EMPTY",
                        lockState = "UNKNOWN",
                        unlockOutputState = "RESET",
                        reasonCodes = DoorUnprovenReasonCodes
                    }).ToArray(),
                    observedAt = fixture.Clock.GetUtcNow()
                }), state, token);
                Assert.Equal("DurableAck", FirstLineType(ack));
            }
            fixture.Context.ChangeTracker.Clear();
            Assert.Equal(JourneyDemandStatuses.Terminated, (await MembershipAsync(fixture, SecondDemandId)).Status);
            // Released back to the stage awaiting the load result, and held.
            await AssertReleasedAsync(fixture, JourneyRuntimeStage.AwaitingLoadResult);
            SlotDoorHoldRow hold = await fixture.Context.SlotDoorHolds.AsNoTracking().SingleAsync(token);
            Assert.Null(hold.ReleasedAt);
            SessionRecoveryRow afterResult = await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(token);
            Assert.Equal(SessionReadiness.RecoveryRequired, afterResult.Readiness);

            // Every other readiness input made good: a fresh report with nothing pending. Only the hold may hold it.
            await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
            {
                WireToGateStore store = new(connection);
                await store.ApplyRecoveryReportAsync(
                    fixture.Options.AgvId, afterResult.SessionGeneration, "f5340000-0000-4000-8000-000000000001",
                    forcedRecoveryGeneration: 0, null, "NONE", [], [], [], token);
                SessionReadinessDecision decision =
                    await store.DecideReadinessAsync(fixture.Options.AgvId, afterResult.SessionGeneration, token);
                Assert.Equal(
                    (SessionReadiness.RecoveryRequired, WireToGateStore.SlotDoorRepairReleaseRequired),
                    (decision.Readiness, decision.ReasonCode));
            }
            fixture.Context.ChangeTracker.Clear();
            Exception? first = await RunRoundAsync(fixture);
            Exception? second = await RunRoundAsync(fixture);
            fixture.Context.ChangeTracker.Clear();
            JourneyRuntimeRow held = await JourneyOfAsync(fixture, FirstDemandId);
            Assert.True(
                await CountAsync(fixture, "PreDepartureSafetyCheck") == checksBefore &&
                held.Stage == JourneyRuntimeStage.AwaitingLoadResult,
                $"Held vehicle advanced. stage={held.Stage} block={held.BlockReasonCode} first={first?.Message} second={second?.Message}");
            Assert.Equal(SessionReadiness.RecoveryRequired,
                (await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(token)).Readiness);

            // Control: the same rounds do ask it to leave once readiness is Ready, so the check above can see movement.
            await fixture.RestoreSessionReadyAsync();
            Exception? third = await RunRoundAsync(fixture);
            Exception? fourth = await RunRoundAsync(fixture);
            fixture.Context.ChangeTracker.Clear();
            JourneyRuntimeRow moved = await JourneyOfAsync(fixture, FirstDemandId);
            Assert.True(moved.Stage is JourneyRuntimeStage.AwaitingStationDeparture or JourneyRuntimeStage.AwaitingDepartureSafety,
                $"control did not advance: stage={moved.Stage} block={moved.BlockReasonCode} {third?.Message} {fourth?.Message}");
        });
    }
}
