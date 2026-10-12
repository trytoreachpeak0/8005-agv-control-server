using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 站点离站等待只属于可继续装货的停靠：卸货停靠卸完、计划里还有下一站时直接去离站核验，不等离站期限
/// （ADR-cross-0055「纯卸货站卸完直接结束」，FR-031 AC-8；control-server#637）。
/// </summary>
/// <remarks>
/// <para>
/// <b>每条用例都显式打开等待。</b>夹具默认把 <c>StationDepartureWaitTimeout</c> 设为零（<c>JourneyRuntimeWorkerTestKit</c>），
/// 零等于「等待关掉、装完当轮离站」——在那个设置下卸货停靠等不等期限根本看不出来，这正是缺陷一直没被照到的原因。
/// </para>
/// <para>
/// 缺陷的形状：离开最后一个取货停靠时起点被置空，卸货停靠到站不种起点，卸完进入离站等待那一轮由
/// <c>StationDepartureWaitIsOverAsync</c> 的 <c>?? now</c> 从卸完那一刻起算，车在卸货站上等满整段期限（生产 5 分钟）。
/// 车上那一版清单的期限是 null，屏上显示「无倒计时」，操作员看不出车在等什么。
/// </para>
/// <para>
/// 混挂站（同一站既卸又装）在服务端是两个相邻停靠：先卸货停靠、后取货停靠，站相同——规划器只并「同站同角色」。
/// 期限由后面那个取货停靠到站时种下，所以卸货停靠不计时与「混合站先完成全部卸货，再启动可继续装货的等待」是同一件事。
/// </para>
/// </remarks>
public sealed class UnloadStopDepartureWaitTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private const string UnloadSafetyResultId = "20000000-0000-4000-8000-000000000637";
    private const string MixedPickupSafetyResultId = "20000000-0000-4000-8000-000000000638";

    /// <summary>
    /// 卸货停靠卸完、后面还有停靠：卸完的那一轮就发离站核验，不在卸货站上等离站期限。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnUnloadStopWithAStopAfterItLeavesWithoutWaitingOutTheStationDeadline()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        fixture.Options.StationDepartureWaitTimeout = Wait;
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(FirstDemandId);
        string extraId = $"{runtime.JourneyId}|EXTRA-UNLOAD";
        fixture.Context.Set<JourneyStopRow>().Add(UnloadStop(
            extraId, runtime.JourneyId, 3, runtime.GateStationId!, runtime.GateStationRiotId, runtime.DispatchZone,
            runtime.CreatedAt));
        await fixture.Context.SaveChangesAsync(token);

        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        await WaitOutTheStationDeadlineAsync(fixture);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        JourneyStopRow unloadStop = await CurrentStopAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyStopRoles.Unload, unloadStop.StopRole);

        await ArriveAtGateAndUnloadAsync(fixture, FirstDemandId);

        // 判据是「这个卸货停靠的离站核验发出去了」：阶段一项只说明不在等，说明不了车被请求离开。
        JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
        unloadStop = await StopAsync(fixture, unloadStop.StopId);
        Assert.True(
            unloadStop.DepartureSafetyCheckMessageId is { } checkId &&
            await fixture.Context.ProtocolOutbox.AsNoTracking().AnyAsync(row => row.MessageId == checkId, token),
            $"The unload stop is done and the vehicle was not asked to leave it. stage={after.Stage} " +
            $"waitStartedAt={after.StationDepartureWaitStartedAt:O} block={after.BlockReasonCode}");
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, after.Stage);
        Assert.Null(after.StationDepartureWaitStartedAt);
    }

    /// <summary>
    /// 取货停靠的离站等待不受影响：装完之后不满期限不离站，满了才离站。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task APickupStopStillWaitsOutTheStationDeadlineAfterItsLoad()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = Wait;
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);

        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        JourneyRuntimeRow settled = await SettleLoadAsync(fixture, FirstDemandId);

        await AssertWaitsUntilItsDeadlineAsync(fixture, settled);
    }

    /// <summary>
    /// 混挂站：同一站先卸后装。卸货那一个停靠卸完就走，接着的取货停靠到站时种下完整期限，装完照常等满。
    /// </summary>
    /// <remarks>
    /// 计划形状 P1(站 12，第一条装) → U1(站 13，第一条卸) → P2(站 13，第二条装) → U2(关卡，第二条卸)，直接在库里排出来，
    /// 形状与规划器落下的一致（同站不同角色不并）。修复若按「站」而不是按「停靠」跳过等待，P2 的等待就会被误跳过，
    /// 这条用例的后半段会红。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AtAStationThatUnloadsThenLoadsTheWaitStartsAtTheLoadingStopNotTheUnloading()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        fixture.Options.StationDepartureWaitTimeout = Wait;
        fixture.Catalog.Set(
            fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)),
            fixture.Demand(SecondDemandId, SecondSublot, Now.AddMinutes(-9), area: "N1-2"));
        fixture.BoxCounts.Set(FirstSublot, 7);
        fixture.BoxCounts.Set(SecondSublot, 7);
        await TickAndRunAsync(fixture);
        await Batch7ThreeStopJourneyTests.AppendSecondDemandAsync(fixture);
        await UnloadTheFirstDemandAtTheSecondPickupStationAsync(fixture);

        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        await WaitOutTheStationDeadlineAsync(fixture);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);

        // 混挂站的卸货那一半：卸完当轮就请求离站。
        await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_GATE");
        JourneyStopRow unloadHalf = await CurrentStopAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyStopRoles.Unload, unloadHalf.StopRole);
        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Unload, SlotBusinessState.Empty);
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, UnloadSafetyResultId);

        // 混挂站的装货那一半：到站种下完整期限，车上那一版清单带着它。
        JourneyRuntimeRow arrived = await ArriveAtCurrentStopAsync(fixture, SecondDemandId, "TO_GATE");
        JourneyStopRow loadHalf = await CurrentStopAsync(fixture, SecondDemandId);
        Assert.Equal(JourneyStopRoles.Pickup, loadHalf.StopRole);
        Assert.Equal(unloadHalf.StationId, loadHalf.StationId);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, arrived.Stage);
        Assert.NotNull(JourneyRuntimeEngine.StationDepartureDeadline(arrived, Wait));

        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        JourneyRuntimeRow settled = await SettleLoadAsync(fixture, SecondDemandId);
        await AssertWaitsUntilItsDeadlineAsync(fixture, settled, MixedPickupSafetyResultId, SecondDemandId);
        Assert.Equal(
            JourneyDemandStatuses.Loaded,
            (await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
                .SingleAsync(row => row.DemandId == SecondDemandId, token)).Status);
    }

    /// <summary>
    /// 旅程终点的卸货停靠：卸完照旧在同一轮完成旅程（它从来不进离站等待，这条守的是修复没把它改走）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheLastUnloadStopStillCompletesTheJourneyInTheRoundItUnloads()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = Wait;
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);

        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        await WaitOutTheStationDeadlineAsync(fixture);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);

        JourneyRuntimeRow after = await ArriveAtGateAndUnloadAsync(fixture, FirstDemandId);

        Assert.Equal(JourneyRuntimeStage.Completed, after.Stage);
    }

    /// <summary>
    /// 卸货停靠跳过的只是时间：车上另一条货的纠错没结，卸完也不离站。
    /// </summary>
    /// <remarks>
    /// 计划形状 P1(第一条装) → P2(第二条装) → U1(第一条卸) → U2(第二条卸)。在 U1 卸第一条时，对仍在车上的第二条开一次纠错。
    /// 纠错闸（<c>LOAD_CORRECTION_IN_PROGRESS</c>）排在角色判断之前；把角色判断挪到它前面，这条红。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnOpenCorrectionOnCargoStillAboardHoldsAnUnloadStopThatIsDone()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        fixture.Options.StationDepartureWaitTimeout = Wait;
        fixture.Catalog.Set(
            fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)),
            fixture.Demand(SecondDemandId, SecondSublot, Now.AddMinutes(-9), area: "N1-2"));
        fixture.BoxCounts.Set(FirstSublot, 7);
        fixture.BoxCounts.Set(SecondSublot, 7);
        await TickAndRunAsync(fixture);
        await Batch7ThreeStopJourneyTests.AppendSecondDemandAsync(fixture);
        string firstUnloadId = await UnloadTheFirstDemandAtAStopOfItsOwnAsync(fixture);

        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        await WaitOutTheStationDeadlineAsync(fixture);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        await ArriveAtCurrentStopAsync(fixture, SecondDemandId, "TO_GATE");
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        JourneyRuntimeRow settled = await SettleLoadAsync(fixture, SecondDemandId);
        await AssertWaitsUntilItsDeadlineAsync(fixture, settled, SecondSafetyResultId, SecondDemandId);

        await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_GATE");
        Assert.Equal(firstUnloadId, (await CurrentStopAsync(fixture, FirstDemandId)).StopId);
        JourneyDemandRow second = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == SecondDemandId, token);
        // 直接写一条开着的纠错：车停在卸货停靠上时服务端不授权纠错，而纠错闸只读这张表（形状同 Batch7StationYieldTests）。
        DateTimeOffset openedAt = fixture.Clock.GetUtcNow();
        fixture.Context.RecoveryWorkflows.Add(new RecoveryWorkflowRow
        {
            WorkflowId = "70000000-0000-4000-8000-000000000637",
            WorkflowType = "LOAD_CORRECTION",
            AgvId = fixture.Options.AgvId,
            DemandId = SecondDemandId,
            SlotOperationAttemptId = second.LoadSlotOperationAttemptId,
            SlotsJson = second.TargetSlotsJson,
            State = RecoveryWorkflowState.CommandPending,
            RequestMessageId = "70000000-0000-4000-8000-000000000638",
            RequestContentHash = new string('f', 64),
            CreatedAt = openedAt,
            UpdatedAt = openedAt
        });
        await fixture.Context.SaveChangesAsync(token);
        fixture.Context.ChangeTracker.Clear();

        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Unload, SlotBusinessState.Empty);
        await TickAndRunAsync(fixture);
        fixture.Clock.Advance(Wait);
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
        JourneyStopRow firstUnload = await StopAsync(fixture, firstUnloadId);
        Assert.Equal(
            JourneyDemandStatuses.Unloaded,
            (await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
                .SingleAsync(row => row.DemandId == FirstDemandId, token)).Status);
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingStationDeparture, "LOAD_CORRECTION_IN_PROGRESS"),
            (after.Stage, after.BlockReasonCode));
        Assert.False(
            firstUnload.DepartureSafetyCheckMessageId is { } checkId &&
            await fixture.Context.ProtocolOutbox.AsNoTracking().AnyAsync(row => row.MessageId == checkId, token),
            "The unload stop was left with a correction open on the cargo still aboard.");
    }

    /// <summary>
    /// 钉住「混挂站 = 同站相邻的一个卸货停靠加一个取货停靠」这个前提：一个停靠只有一个角色，需求只挂一个取货停靠、一个卸货停靠。
    /// </summary>
    /// <remarks>
    /// 修复按停靠角色跳过离站等待（<c>StopRole == Unload</c>）。这只在「卸货停靠上不会有可继续装货的作业」时成立，
    /// 而那件事今天由数据形状保证：<see cref="JourneyStopRow.StopRole"/> 是单值，<see cref="JourneyDemandRow"/> 只有
    /// <c>PickupStopId</c> 与 <c>UnloadStopId</c>，规划器只并「同站同角色」的停靠（<c>EnRouteAppendPlanner</c> 的插入位注释）。
    /// 哪天加了第三种带作业的角色或混合角色，这条红，提醒回来重判卸货停靠的离站等待（control-server#637）。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public void AStationThatUnloadsAndLoadsIsTwoStopsBecauseAStopHasOneRole()
    {
        string[] roles = [.. typeof(JourneyStopRoles)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)];
        Assert.Equal(["CHARGER", "PICKUP", "UNLOAD", "WAITING_POINT"], roles);

        Assert.Equal(typeof(string), typeof(JourneyStopRow).GetProperty(nameof(JourneyStopRow.StopRole))!.PropertyType);
        string[] stopReferences = [.. typeof(JourneyDemandRow).GetProperties()
            .Where(property => property.Name.EndsWith("StopId", StringComparison.Ordinal))
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)];
        Assert.Equal([nameof(JourneyDemandRow.PickupStopId), nameof(JourneyDemandRow.UnloadStopId)], stopReferences);
    }

    /// <summary>装完停在离站等待的取货停靠：期限之前一轮仍在等，到期那一轮发出离站核验。</summary>
    private static async Task AssertWaitsUntilItsDeadlineAsync(
        RuntimeFixture fixture,
        JourneyRuntimeRow settled,
        string? answerWith = null,
        string demandId = FirstDemandId)
    {
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, settled.Stage);
        DateTimeOffset deadline = JourneyRuntimeEngine.StationDepartureDeadline(settled, Wait)
            ?? throw new InvalidOperationException("A loaded pickup stop has no station deadline.");

        fixture.Clock.Advance(deadline - fixture.Clock.GetUtcNow() - TimeSpan.FromSeconds(2));
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await JourneyOfAsync(fixture, demandId)).Stage);

        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await JourneyOfAsync(fixture, demandId)).Stage);
        if (answerWith is not null)
        {
            await AnswerDepartureSafetyAsync(fixture, demandId, answerWith);
        }
    }

    /// <summary>装完停在取货停靠的离站等待里，把钟拨过期限，让车请求离站。</summary>
    private static async Task WaitOutTheStationDeadlineAsync(RuntimeFixture fixture)
    {
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        fixture.Clock.Advance(Wait);
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
    }

    /// <summary>
    /// 把 P1, P2, U 排成 P1, U1, P2, U：第一条改在第二个取货站卸，卸货停靠新开一个，第二条仍在原卸货停靠卸。
    /// </summary>
    private static async Task UnloadTheFirstDemandAtTheSecondPickupStationAsync(RuntimeFixture fixture)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(FirstDemandId);
        JourneyDemandRow first = await fixture.Context.Set<JourneyDemandRow>()
            .SingleAsync(row => row.DemandId == FirstDemandId, token);
        JourneyDemandRow second = await fixture.Context.Set<JourneyDemandRow>()
            .SingleAsync(row => row.DemandId == SecondDemandId, token);
        JourneyStopRow secondPickup = await StopAsync(fixture, second.PickupStopId, tracked: true);
        JourneyStopRow unload = await StopAsync(fixture, second.UnloadStopId, tracked: true);
        Assert.Equal(first.UnloadStopId, unload.StopId);

        string unloadHalfId = $"{runtime.JourneyId}|MIXED-UNLOAD";
        fixture.Context.Set<JourneyStopRow>().Add(UnloadStop(
            unloadHalfId, runtime.JourneyId, secondPickup.Sequence, secondPickup.StationId, secondPickup.StationRiotId,
            secondPickup.DispatchZone, runtime.CreatedAt));
        unload.Sequence += 2;
        secondPickup.Sequence += 1;
        first.UnloadStopId = unloadHalfId;
        await fixture.Context.SaveChangesAsync(token);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>
    /// 把 P1, P2, U 排成 P1, P2, U1, U：第一条改在一个自己的卸货停靠上先卸（站同关卡），第二条仍在原卸货停靠卸。返回 U1 的 id。
    /// </summary>
    private static async Task<string> UnloadTheFirstDemandAtAStopOfItsOwnAsync(RuntimeFixture fixture)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(FirstDemandId);
        JourneyDemandRow first = await fixture.Context.Set<JourneyDemandRow>()
            .SingleAsync(row => row.DemandId == FirstDemandId, token);
        JourneyStopRow unload = await StopAsync(fixture, first.UnloadStopId, tracked: true);

        string firstUnloadId = $"{runtime.JourneyId}|FIRST-UNLOAD";
        fixture.Context.Set<JourneyStopRow>().Add(UnloadStop(
            firstUnloadId, runtime.JourneyId, unload.Sequence, unload.StationId, unload.StationRiotId,
            unload.DispatchZone, runtime.CreatedAt));
        unload.Sequence += 1;
        first.UnloadStopId = firstUnloadId;
        await fixture.Context.SaveChangesAsync(token);
        fixture.Context.ChangeTracker.Clear();
        return firstUnloadId;
    }

    private static async Task<JourneyStopRow> StopAsync(RuntimeFixture fixture, string stopId, bool tracked = false)
    {
        IQueryable<JourneyStopRow> stops = fixture.Context.Set<JourneyStopRow>();
        return await (tracked ? stops : stops.AsNoTracking())
            .SingleAsync(row => row.StopId == stopId, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 一个卸货停靠，形状与 <c>SingleDemandJourneyShape</c> 写下的一致：录入请求与离站核验的两个 id 留空，
    /// 由引擎第一次要离站时补上。
    /// </summary>
    private static JourneyStopRow UnloadStop(
        string stopId,
        string journeyId,
        int sequence,
        string stationId,
        int stationRiotId,
        string dispatchZone,
        DateTimeOffset createdAt) => new()
        {
            StopId = stopId,
            JourneyId = journeyId,
            Sequence = sequence,
            StopRole = JourneyStopRoles.Unload,
            StationId = stationId,
            StationRiotId = stationRiotId,
            DispatchZone = dispatchZone,
            OperationSessionId = JourneyPlanBuilder.StableGuid(stopId, "session"),
            MovementLegId = JourneyPlanBuilder.StableGuid(stopId, "leg"),
            UpperId = $"W2G-{stopId}",
            VehicleBusinessMessageId = JourneyPlanBuilder.StableGuid(stopId, "vehicle-state"),
            WorklistMessageId = JourneyPlanBuilder.StableGuid(stopId, "worklist"),
            PlanMessageId = JourneyPlanBuilder.StableGuid(stopId, "plan"),
            Status = JourneyStopStatuses.Pending,
            CreatedAt = createdAt
        };
}
