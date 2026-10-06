using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.Batch7ThreeStopJourneyTests;
using static ControlServer.Tests.JourneyRuntimeWorkerLoadDeadlineTests;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 确定的装货失败（ADR-cross-0058 决定 5）终结的是第二个取货停靠上唯一那条需求、车上还带着第一站的货时，车离开这一站
/// （control-server#291 的 S2，来自 #289 独立审查）。
/// </summary>
/// <remarks>
/// <para>
/// <c>TrySettleDeterminateLoadFailureAsync</c> 经 <c>PickupStopTermination</c> 终结那一条；旅程还带着别的需求时不收尾，而阶段原来一直
/// 留在 <c>AwaitingLoadResult</c>。这一站已经没有在装的、也没有装上的，下一轮起每轮抛
/// <c>waits for a load result with no demand loading at its stop.</c>——cs#487 之后不再拖停别的车，但这辆车自己永远不走，
/// 车上第一站的货也永远不卸。站点期限结束同一种停靠时（<c>TryEndStopAtStationDeadlineAsync</c>）早就把阶段交给离站那一段，这里照它做。
/// </para>
/// <para>
/// 防御路径：v2 车载端过了期限不报这种 FAILED（8005-agv-program#55），协议允许别的车载端版本报。L1 是它唯一的证明。
/// </para>
/// </remarks>
public sealed class LoadEndedAtASecondPickupTests
{
    /// <summary>第二条需求的 AREA，解析到站 13，与第一条的站 12 不同（同 <c>Batch7ThreeStopJourneyTests</c>）。</summary>
    private const string SecondPickupArea = "N1-2";

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ADeterminateLoadFailureEndingTheOnlyDemandOfASecondPickupSendsTheVehicleOnWithWhatItCarries()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        fixture.Catalog.Set(
            fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)),
            fixture.Demand(SecondDemandId, SecondSublot, Now.AddMinutes(-9), area: SecondPickupArea));
        fixture.BoxCounts.Set(FirstSublot, 7);
        fixture.BoxCounts.Set(SecondSublot, 7);
        await TickAndRunAsync(fixture);
        await AppendSecondDemandAsync(fixture);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        await ArriveAtCurrentStopAsync(fixture, SecondDemandId, "TO_GATE");
        JourneyStopRow secondPickup = await CurrentStopAsync(fixture, SecondDemandId);
        Assert.Equal(JourneyStopRoles.Pickup, secondPickup.StopRole);

        // 期限从到站那一轮起算；放在到站之后设，第一站不受它影响。
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        fixture.Clock.Advance(TimeSpan.FromSeconds(11));
        await ReportLoadResultAsync(
            fixture, DeterminateFailureSlots(fixture, "OPERATOR_TIMEOUT", SecondDemandId), SecondDemandId);
        Assert.Equal(
            StationOperationStatus.Failed,
            (await fixture.Context.StationOperations.AsNoTracking().SingleAsync(
                row => row.DemandId == SecondDemandId && row.OperationType == SlotOperationType.Load, token)).Status);

        Exception? settling = await RunRoundAsync(fixture);
        Assert.Equal(DemandExecutionStatus.Cancelled, (await fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(row => row.DemandId == SecondDemandId, token)).Status);
        Exception? following = await RunRoundAsync(fixture);

        JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(
            secondPickup.DepartureSafetyCheckMessageId is { } checkId &&
            await fixture.Context.ProtocolOutbox.AsNoTracking().AnyAsync(row => row.MessageId == checkId, token),
            $"The stop ended and the vehicle was never asked to leave it. stage={after.Stage} block={after.BlockReasonCode} " +
            $"settling={settling?.Message} following={following?.Message}");
        Assert.Equal(
            JourneyDemandStatuses.Loaded,
            (await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
                .SingleAsync(row => row.DemandId == FirstDemandId, token)).Status);
    }

    /// <summary>
    /// 同一个形状的第二条来路：操作员在第二个取货站<b>装货途中</b>按「取消装货」，车证明仓位全空，取消结果经恢复协调器终结这一条。
    /// 授权取消不阻塞旅程，所以终结之后阶段同样留在 <c>AwaitingLoadResult</c>。与确定的装货失败不同，这一条是 v2 车载端真会走的路。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancellationWhileLoadingTheOnlyDemandOfASecondPickupSendsTheVehicleOnWithWhatItCarries()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        JourneyStopRow secondPickup = await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture);
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        StationOperationRow load = await fixture.Context.StationOperations.AsNoTracking().SingleAsync(
            row => row.DemandId == SecondDemandId && row.OperationType == SlotOperationType.Load, token);
        int[] slots = JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson) ?? [];

        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer);
            OnboardConnectionState state = StopEndedJourneyContinuesTests.Connection(fixture);
            const string cancellationId = "d3000000-0000-4000-8000-000000000001";
            object @operator = new { operatorId = "OP-001", verificationMethod = "BADGE", verifiedAt = fixture.Clock.GetUtcNow() };
            string authorization = await processor.ProcessAsync(
                StopEndedJourneyContinuesTests.Envelope(
                    fixture, "d3000000-0000-4000-8000-000000000002", "LoadCancellationStartRequested", new
                    {
                        cancellationId,
                        demandId = SecondDemandId,
                        slotOperationAttemptId = load.SlotOperationAttemptId,
                        @operator,
                        reason = "Nothing to load at this stop."
                    }),
                state,
                token);
            Assert.Equal(
                "AUTHORIZED",
                StopEndedJourneyContinuesTests.FirstLinePayload(authorization).GetProperty("decision").GetString());
            await processor.ProcessAsync(
                StopEndedJourneyContinuesTests.Envelope(
                    fixture, "d3000000-0000-4000-8000-000000000003", "LoadCancellationResult", new
                    {
                        cancellationId,
                        demandId = SecondDemandId,
                        slotOperationAttemptId = load.SlotOperationAttemptId,
                        overallOutcome = "ALL_EMPTY",
                        slotResults = slots.Select(slot => new
                        {
                            slotNo = slot,
                            outcome = "COMPLETED",
                            finalPhysicalState = "EMPTY",
                            lockState = "LOCKED",
                            unlockOutputState = "RESET",
                            reasonCodes = Array.Empty<string>()
                        }).ToArray(),
                        observedAt = fixture.Clock.GetUtcNow()
                    }),
                state,
                token);
        }
        fixture.Context.ChangeTracker.Clear();
        // 前提：乙被取消终结，旅程因还带着甲而继续，阶段仍是等装货结果。
        Assert.Equal(JourneyDemandStatuses.Terminated, (await StopEndedJourneyContinuesTests.MembershipAsync(fixture, SecondDemandId)).Status);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        Exception? first = await RunRoundAsync(fixture);
        Exception? second = await RunRoundAsync(fixture);

        JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(
            secondPickup.DepartureSafetyCheckMessageId is { } checkId &&
            await fixture.Context.ProtocolOutbox.AsNoTracking().AnyAsync(row => row.MessageId == checkId, token),
            $"The stop ended and the vehicle was never asked to leave it. stage={after.Stage} block={after.BlockReasonCode} " +
            $"first={first?.Message} second={second?.Message}");
        Assert.Equal(JourneyDemandStatuses.Loaded, (await StopEndedJourneyContinuesTests.MembershipAsync(fixture, FirstDemandId)).Status);
    }

    private static async Task<Exception?> RunRoundAsync(RuntimeFixture fixture)
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        return await Record.ExceptionAsync(() => fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken));
    }
}
