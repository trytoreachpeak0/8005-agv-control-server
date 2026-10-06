using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerLoadDeadlineTests;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.StopEndedJourneyContinuesTests;

namespace ControlServer.Tests;

/// <summary>
/// 第二个取货停靠上在装的那一条被终结、车上还带着第一站的货时，旅程接着走（control-server#291 的 S2 及其同形来路）。
/// </summary>
/// <remarks>
/// <para>
/// 终结走 <c>PickupStopTermination</c>；旅程还带着别的需求时不收尾，而阶段原来一直留在 <c>AwaitingLoadResult</c>。这一站已经没有在装的、
/// 也没有装上的，下一轮起每轮抛 <c>waits for a load result with no demand loading at its stop.</c>——cs#487 之后不再拖停别的车，
/// 但这辆车自己永远不走，车上第一站的货也永远不卸。
/// </para>
/// <para>
/// 两道修：确定的装货失败在终结的同一次保存里把阶段带走（<c>TrySettleDeterminateLoadFailureAsync</c>，新发生的不再进卡住状态）；
/// 等装货结果那一段认出「没有在装、没有装上、本站有一条 TERMINATED」并接着走（兜住不带走阶段的来路——恢复协调器的装货途中取消——
/// 以及已经卡在这个状态的库）。
/// </para>
/// </remarks>
public sealed class LoadEndedAtASecondPickupTests
{
    private const string ThirdSubmissionId = "20000000-0000-4000-8000-000000000021";

    /// <summary>
    /// S2：确定的装货失败（ADR-cross-0058 决定 5）终结了第二个取货停靠上唯一那条。防御路径：v2 车载端过了期限不报这种 FAILED
    /// （8005-agv-program#55），协议允许别的车载端版本报，L1 是它唯一的证明。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ADeterminateLoadFailureEndingTheOnlyDemandOfASecondPickupSendsTheVehicleOnWithWhatItCarries()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyStopRow secondPickup = await ArriveAtTheSecondPickupAsync(fixture);
        await LoadTheSecondDemandAndFailItDeterminatelyAsync(fixture);

        Exception? settling = await RunRoundAsync(fixture);
        Assert.Equal(DemandExecutionStatus.Cancelled, (await DemandAsync(fixture, SecondDemandId)).Status);
        Exception? following = await RunRoundAsync(fixture);

        await AssertTheVehicleWasAskedToLeaveAsync(fixture, secondPickup, settling, following);
    }

    /// <summary>
    /// 同一个形状的第二条来路：操作员在第二个取货站<b>装货途中</b>按「取消装货」，车证明仓位全空，取消结果经恢复协调器终结这一条。
    /// 授权取消不阻塞旅程，协调器也不动阶段，所以终结之后阶段同样留在 <c>AwaitingLoadResult</c>。这一条是 v2 车载端真会走的路。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancellationWhileLoadingTheOnlyDemandOfASecondPickupSendsTheVehicleOnWithWhatItCarries()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyStopRow secondPickup = await ArriveAtTheSecondPickupAsync(fixture);
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        await CancelTheSecondDemandWhileLoadingAsync(fixture);

        Exception? first = await RunRoundAsync(fixture);
        Exception? second = await RunRoundAsync(fixture);

        await AssertTheVehicleWasAskedToLeaveAsync(fixture, secondPickup, first, second);
    }

    /// <summary>
    /// 库里已经卡在这个状态（修复之前留下的）：阶段 <c>AwaitingLoadResult</c>，本站那一条已 <c>TERMINATED</c>、需求已取消，没有在装的、
    /// 没有装上的。升级之后下一轮就续上。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AJourneyAlreadyStuckWithItsOnlyDemandHereTerminatedLeavesOnTheNextRound()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        JourneyStopRow secondPickup = await ArriveAtTheSecondPickupAsync(fixture);
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);

        await using (ControlServerDbContext context = fixture.OpenConnectionContext())
        {
            (await context.Set<JourneyDemandRow>().SingleAsync(row => row.DemandId == SecondDemandId, token)).Status =
                JourneyDemandStatuses.Terminated;
            (await context.AcceptedDemands.SingleAsync(row => row.DemandId == SecondDemandId, token)).Status =
                DemandExecutionStatus.Cancelled;
            (await context.StationOperations.SingleAsync(
                row => row.DemandId == SecondDemandId && row.OperationType == SlotOperationType.Load, token)).Status =
                StationOperationStatus.Cancelled;
            await context.SaveChangesAsync(token);
        }
        fixture.Context.ChangeTracker.Clear();
        await fixture.RecreateEngineAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        Exception? first = await RunRoundAsync(fixture);
        Exception? second = await RunRoundAsync(fixture);

        await AssertTheVehicleWasAskedToLeaveAsync(fixture, secondPickup, first, second);
    }

    /// <summary>
    /// 装货途中取消了第二个取货站上的一条，同站还有一条没装：回到等录入，发出下一版清单与录入请求，第三条照常录入、下装货命令。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancellationWhileLoadingOneOfTwoDemandsOfASecondPickupGoesBackToTheEntryForTheOther()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await ArriveAtTheSecondPickupAsync(fixture, thirdAtTheSecondPickup: true);
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        await CancelTheSecondDemandWhileLoadingAsync(fixture);

        Exception? round = await RunRoundAsync(fixture);
        JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(
            after.Stage == JourneyRuntimeStage.AwaitingSublot,
            $"The stop still has a demand to load, so the journey should be back at the entry. stage={after.Stage} round={round?.Message}");

        await EnterSublotAsync(fixture, FirstDemandId, ThirdSublot, ThirdSubmissionId);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        Assert.Equal(JourneyDemandStatuses.Loading, (await MembershipAsync(fixture, ThirdDemandId)).Status);
    }

    /// <summary>
    /// 确定的装货失败终结了第二个取货站上的一条，同站还有一条没装：阶段在同一次保存里回到等录入（期限已过），随后由站点期限的出口
    /// 结束本站——终结剩下那一条、车带着第一站的货离站。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ADeterminateLoadFailureOfOneOfTwoDemandsOfASecondPickupHandsTheStopToItsDeadline()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyStopRow secondPickup = await ArriveAtTheSecondPickupAsync(fixture, thirdAtTheSecondPickup: true);
        await LoadTheSecondDemandAndFailItDeterminatelyAsync(fixture);

        Exception? settling = await RunRoundAsync(fixture);
        JourneyRuntimeRow settled = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(
            settled.Stage == JourneyRuntimeStage.AwaitingSublot,
            $"The stop still has a demand to load, so the settlement should hand it back to the entry. stage={settled.Stage} round={settling?.Message}");

        await fixture.ProveSlotDoorsClosedAsync();
        await fixture.HearFromPeerAsync();
        Exception? ending = await RunRoundAsync(fixture);
        await fixture.HearFromPeerAsync();
        Exception? leaving = await RunRoundAsync(fixture);

        Assert.Equal(JourneyDemandStatuses.Terminated, (await MembershipAsync(fixture, ThirdDemandId)).Status);
        await AssertTheVehicleWasAskedToLeaveAsync(fixture, secondPickup, ending, leaving);
    }

    /// <summary>第二条在第二个取货站录入、下装货命令，过了十秒期限之后车报确定的失败（首仓 FAILED/OPERATOR_TIMEOUT，其余 NOT_STARTED）。</summary>
    private static async Task LoadTheSecondDemandAndFailItDeterminatelyAsync(RuntimeFixture fixture)
    {
        // 期限从到站那一轮起算；放在到站之后设，第一站不受它影响。
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        fixture.Clock.Advance(TimeSpan.FromSeconds(11));
        await ReportLoadResultAsync(
            fixture, DeterminateFailureSlots(fixture, "OPERATOR_TIMEOUT", SecondDemandId), SecondDemandId);
        Assert.Equal(StationOperationStatus.Failed, (await SecondLoadAsync(fixture)).Status);
    }

    /// <summary>车在装第二条时操作员按「取消装货」，授权之后车报仓位全空。经入站处理器，像真连接那样。</summary>
    private static async Task CancelTheSecondDemandWhileLoadingAsync(RuntimeFixture fixture)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        StationOperationRow load = await SecondLoadAsync(fixture);
        int[] slots = JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson) ?? [];
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer);
            OnboardConnectionState state = Connection(fixture);
            const string cancellationId = "d3000000-0000-4000-8000-000000000001";
            string authorization = await processor.ProcessAsync(
                Envelope(fixture, "d3000000-0000-4000-8000-000000000002", "LoadCancellationStartRequested", new
                {
                    cancellationId,
                    demandId = SecondDemandId,
                    slotOperationAttemptId = load.SlotOperationAttemptId,
                    @operator = new { operatorId = "OP-001", verificationMethod = "BADGE", verifiedAt = fixture.Clock.GetUtcNow() },
                    reason = "Nothing to load at this stop."
                }),
                state,
                token);
            Assert.Equal("AUTHORIZED", FirstLinePayload(authorization).GetProperty("decision").GetString());
            await processor.ProcessAsync(
                Envelope(fixture, "d3000000-0000-4000-8000-000000000003", "LoadCancellationResult", new
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
        // 前提：第二条被取消终结，旅程因还带着第一条而继续，阶段仍是等装货结果。
        Assert.Equal(JourneyDemandStatuses.Terminated, (await MembershipAsync(fixture, SecondDemandId)).Status);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
    }

    /// <summary>判据与 <c>AfterTheStationDeadlineEndsASecondPickupTheVehicleLeavesWithWhatItCarries</c> 相同：这个停靠发出了离站核验，第一条仍在车上。</summary>
    private static async Task AssertTheVehicleWasAskedToLeaveAsync(
        RuntimeFixture fixture, JourneyStopRow secondPickup, Exception? first, Exception? second)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(
            secondPickup.DepartureSafetyCheckMessageId is { } checkId &&
            await fixture.Context.ProtocolOutbox.AsNoTracking().AnyAsync(row => row.MessageId == checkId, token),
            $"The stop ended and the vehicle was never asked to leave it. stage={after.Stage} block={after.BlockReasonCode} " +
            $"first={first?.Message} second={second?.Message}");
        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, FirstDemandId)).Status);
    }

    private static Task<StationOperationRow> SecondLoadAsync(RuntimeFixture fixture) =>
        fixture.Context.StationOperations.AsNoTracking().SingleAsync(
            row => row.DemandId == SecondDemandId && row.OperationType == SlotOperationType.Load,
            TestContext.Current.CancellationToken);

    private static Task<AcceptedDemandRow> DemandAsync(RuntimeFixture fixture, string demandId) =>
        fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(
            row => row.DemandId == demandId, TestContext.Current.CancellationToken);

    private static async Task<Exception?> RunRoundAsync(RuntimeFixture fixture)
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        return await Record.ExceptionAsync(() => fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken));
    }
}
