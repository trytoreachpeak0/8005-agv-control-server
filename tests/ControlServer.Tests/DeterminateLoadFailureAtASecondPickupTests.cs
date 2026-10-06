using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
public sealed class DeterminateLoadFailureAtASecondPickupTests
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

    private static async Task<Exception?> RunRoundAsync(RuntimeFixture fixture)
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        return await Record.ExceptionAsync(() => fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken));
    }
}
