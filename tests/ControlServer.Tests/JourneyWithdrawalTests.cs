using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.ForeignOrders;
using ControlServer.Host.Runtime.Withdrawal;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 人工撤下一趟旅程（control-server#771）的核心：演练、确认码、撤下写下的每一件事，以及每一条拒绝。
/// </summary>
/// <remarks>
/// <para>
/// <b>agv03 那一形</b>（10-19，hmi#370）：一个取货站两条需求，第一条已扫码、下了装货命令，结果以 UNKNOWN 收尾，旅程
/// <c>Blocked/LOAD_RESULT_REQUIRES_RECOVERY</c>，第二条还没装。车上的恢复开关关着，车上没有出口；修之前服务端也没有，20 号关闸因这趟旅程被拒。
/// 现场确认那个仓是空的、门锁着之后，撤下：旅程收尾，两条都退回待派、不抑制，那一次装货结成取消，车不再因它而 RecoveryRequired。
/// </para>
/// <para>
/// <b>agv01 那一形</b>（10-18）：车开往取货站途中车载端离线。没有任何仓位操作，撤下不要仓位事实。
/// </para>
/// <para>
/// 全部在真 SQLite 上、经真实引擎造出状态，撤下本身调核心（<see cref="JourneyWithdrawal"/>），与 FieldOps 直接写库那条路同一段代码。
/// </para>
/// </remarks>
public sealed class JourneyWithdrawalTests
{
    private const string Operator = "OP-771";
    private const string Reason = "agv03 装货结果未知，现场确认 5 号仓空、门锁着，篮子在机台上";
    private const string SiteVerification = "现场核实记录 2026-10-19 Coordinator13";
    private const string RiotVerification = "RIoT 截图：agv03 无未结束订单";
    private const string CargoLocation = "在机台上";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------------------------------------
    // agv03 那一形：装货结果 UNKNOWN、旅程阻塞，本站还有一条没装。
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AJourneyBlockedOnAnUnknownLoadIsWithdrawnAndBothDemandsGoBackToTheBacklogUnsuppressed()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        JourneyWithdrawalRequest request = await RequestAsync(fixture, blocked);

        JourneyWithdrawalResult planned = await JourneyWithdrawal.PlanAsync(fixture.Context, request, Token);
        Assert.Equal(("PLANNED", 0), (planned.Outcome, planned.Codes.Count));
        Assert.NotNull(planned.ConfirmationCode);
        JourneyWithdrawalResult withdrawn = await WithdrawAsync(fixture, request with { ConfirmationCode = planned.ConfirmationCode });

        Assert.True(withdrawn.Outcome == "WITHDRAWN", string.Join(",", withdrawn.Codes));
        JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal((JourneyRuntimeStage.Completed, JourneyWithdrawal.WithdrawnReason), (after.Stage, after.BlockReasonCode));
        Assert.Equal(
            StationOperationStatus.Cancelled,
            (await fixture.Context.StationOperations.AsNoTracking().SingleAsync(row => row.DemandId == FirstDemandId, Token)).Status);
        foreach (string demandId in new[] { FirstDemandId, SecondDemandId })
        {
            AcceptedDemandRow demand = await fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(row => row.DemandId == demandId, Token);
            Assert.Equal(DemandExecutionStatus.Accepted, demand.Status);
            JourneyDemandRow membership = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
                .SingleAsync(row => row.DemandId == demandId, Token);
            Assert.Equal(DemandJourneyLookup.ReleasedForRedispatchReason, membership.RemovalReason);
        }
        Assert.Equal(
            [FirstDemandId, SecondDemandId],
            (await DemandJourneyLookup.ReleasedForRedispatch(fixture.Context).AsNoTracking().Select(row => row.DemandId).ToArrayAsync(Token))
                .Order(StringComparer.Ordinal));
        Assert.Empty(await fixture.Context.Set<TransportDemandSuppressionRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.False(await new WireToGateStore(fixture.Context).OperationNeedsRecoveryAsync(fixture.Options.AgvId, Token));
    }

    /// <summary>
    /// agv03 在 10-19 16:36 v2 重启之后的样子（gen42）：车重连时在恢复报告里报「还有一次没结的操作」，服务端只拿掉已结的，那一次仍是
    /// RecoveryRequired，所以它留在待核实事实里，会话原因码排成 <c>PENDING_FACT_RECONCILIATION_REQUIRED</c>（排在
    /// <c>OPERATION_RECOVERY_REQUIRED</c> 之前，<c>WireToGateStore.GetRecoveryReason</c>）。底下是同一件事。撤下不看这个码；撤下之后那一次结成取消，
    /// 待核实里拿掉它（与握手时同一条规则），会话不再因它而 RecoveryRequired。
    /// </summary>
    [Fact]
    public async Task AfterAReconnectThePendingFactIsTheSameUnknownLoadAndTheWithdrawalClearsIt()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        string attempt = (await OperationOfAsync(fixture, FirstDemandId)).SlotOperationAttemptId;
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.SingleAsync(Token);
        session.PendingAttemptIdsJson = JsonSerializer.Serialize(new List<string> { attempt });
        // The vehicle also reports it as its unsettled attempt (hmi PR #376 item a): the one this exit settles, so allowed.
        session.UnsettledSlotOperationAttemptId = attempt;
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        WireToGateStore store = new(fixture.Context);
        Assert.Equal(
            "PENDING_FACT_RECONCILIATION_REQUIRED",
            (await store.DecideReadinessAsync(fixture.Options.AgvId, session.SessionGeneration, Token)).ReasonCode);
        fixture.Context.ChangeTracker.Clear();

        await PlanAndWithdrawAsync(fixture, await RequestAsync(fixture, blocked));

        SessionRecoveryRow after = await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(Token);
        Assert.Equal("[]", after.PendingAttemptIdsJson);
        Assert.NotEqual("PENDING_FACT_RECONCILIATION_REQUIRED", after.ReasonCode);
        Assert.NotEqual("OPERATION_RECOVERY_REQUIRED", after.ReasonCode);
    }

    /// <summary>
    /// 车最近报的未结操作不是要撤的这一次（onboard-hmi PR #376 第 a 项）：撤下之后车仍挂着一次服务端没结的操作，下一条命令覆盖不了，拒绝。
    /// </summary>
    [Fact]
    public async Task AnotherUnsettledAttemptReportedByTheVehicleRefusesTheWithdrawal()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.SingleAsync(Token);
        session.UnsettledSlotOperationAttemptId = "77100000-0000-4000-8000-0000000000aa";
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        await AssertRefusedEverywhereAsync(fixture, await RequestAsync(fixture, blocked), JourneyWithdrawal.UnsettledAttemptElsewhere);
    }

    /// <summary>
    /// 撤下之后车重连、补发那条待答的 UNKNOWN 结果（onboard-hmi PR #376 第 e 项）：服务端认出是同一条，只确认，不改任何东西——
    /// 那次操作仍是取消，旅程仍是收尾，需求仍在待派。
    /// </summary>
    [Fact]
    public async Task TheVehiclesReplayOfTheUnknownResultAfterAWithdrawalChangesNothing()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        StationOperationRow operation = await OperationOfAsync(fixture, FirstDemandId);
        OperationResultRow sent = await fixture.Context.OperationResults.AsNoTracking()
            .SingleAsync(row => row.SlotOperationAttemptId == operation.SlotOperationAttemptId, Token);
        await PlanAndWithdrawAsync(fixture, await RequestAsync(fixture, blocked));

        OperationResultDisposition replay = await new WireToGateStore(fixture.Context).ApplyOperationResultAsync(
            new StationOperationResult(
                sent.ResultId,
                sent.SlotOperationAttemptId,
                operation.DemandId,
                SlotOperationType.Load,
                sent.OverallOutcome,
                JsonSerializer.Deserialize<SlotPhysicalEvidence[]>(sent.EvidenceJson)!,
                false,
                sent.ObservedAt,
                sent.ResultContentSha256,
                sent.ContentHash),
            fixture.Options.AgvId,
            sent.ForcedRecoveryGeneration,
            Token);
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(OperationResultDisposition.Replay, replay);
        Assert.Equal(
            StationOperationStatus.Cancelled,
            (await fixture.Context.StationOperations.AsNoTracking()
                .SingleAsync(row => row.SlotOperationAttemptId == operation.SlotOperationAttemptId, Token)).Status);
        Assert.Equal(JourneyRuntimeStage.Completed, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        Assert.Equal(
            DemandExecutionStatus.Accepted,
            (await fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(row => row.DemandId == FirstDemandId, Token)).Status);
        Assert.False(await new WireToGateStore(fixture.Context).OperationNeedsRecoveryAsync(fixture.Options.AgvId, Token));
    }

    /// <summary>车要收的收尾快照落进发件箱，空清单说「本站因任务释放结束」：这批会重新派车，不是丢了。</summary>
    [Fact]
    public async Task TheVehicleIsToldTheStopEndedBecauseTheDemandsWereReleased()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);

        await PlanAndWithdrawAsync(fixture, await RequestAsync(fixture, blocked));

        JsonElement worklist = await fixture.OutboxPayloadAsync(JourneyClosure.SnapshotMessageIds(blocked.JourneyId)[0]);
        Assert.Equal("DEMAND_RELEASED", worklist.GetProperty("stopEndedReason").GetString());
        Assert.Equal(0, worklist.GetProperty("items").GetArrayLength());
    }

    /// <summary>每一次撤下写一条管理员审计：谁、为什么、现场核实了什么、计划是什么。</summary>
    [Fact]
    public async Task AWithdrawalWritesWhoAndWhyIntoTheAdministratorAudit()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);

        JourneyWithdrawalResult withdrawn = await PlanAndWithdrawAsync(fixture, await RequestAsync(fixture, blocked));

        AdministratorAuditRecordRow audit = await fixture.Context.Set<AdministratorAuditRecordRow>().AsNoTracking()
            .SingleAsync(row => row.Action == JourneyWithdrawal.AuditAction, Token);
        Assert.Equal(withdrawn.AuditRecordId, audit.AuditRecordId);
        Assert.Equal((GovernedObjectKind.Journey, blocked.JourneyId, GovernanceActionOutcome.Succeeded), (audit.ObjectKind, audit.ObjectId, audit.Outcome));
        foreach (string said in new[] { Operator, Reason, SiteVerification, RiotVerification, CargoLocation, FirstDemandId, SecondDemandId })
        {
            Assert.Contains(said, audit.DetailJson, StringComparison.Ordinal);
        }
    }

    /// <summary>演练只读：一行不写、不写审计，可以在服务端还跑着的时候先看一眼。</summary>
    [Fact]
    public async Task APlanWritesNothing()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        int savesBefore = fixture.SaveChanges.Count;

        JourneyWithdrawalResult planned = await JourneyWithdrawal.PlanAsync(fixture.Context, await RequestAsync(fixture, blocked), Token);

        Assert.Equal("PLANNED", planned.Outcome);
        Assert.Null(planned.AuditRecordId);
        Assert.Equal(savesBefore, fixture.SaveChanges.Count);
        Assert.False(fixture.Context.ChangeTracker.HasChanges());
        Assert.Equal(JourneyRuntimeStage.Blocked, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
    }

    /// <summary>
    /// 演练给出的计划里列着每条需求的去向与那一次装货，确认码绑住它们（记忆「二次确认的键要含比对对象」）：同一状态算两次，码相同。
    /// </summary>
    [Fact]
    public async Task ThePlanNamesEveryDemandAndTheUnresolvedLoadAndItsCodeIsStable()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        JourneyWithdrawalRequest request = await RequestAsync(fixture, blocked);

        JourneyWithdrawalResult first = await JourneyWithdrawal.PlanAsync(fixture.Context, request, Token);
        JourneyWithdrawalResult second = await JourneyWithdrawal.PlanAsync(fixture.Context, request, Token);

        Assert.Equal(first.ConfirmationCode, second.ConfirmationCode);
        JourneyWithdrawalPlan plan = first.Plan!;
        Assert.Equal(
            [(FirstDemandId, JourneyWithdrawal.ReleaseToBacklog), (SecondDemandId, JourneyWithdrawal.ReleaseToBacklog)],
            plan.Demands.Select(item => (item.DemandId, item.Disposition)).OrderBy(item => item.DemandId, StringComparer.Ordinal));
        Assert.Equal(FirstDemandId, plan.UnresolvedLoad!.DemandId);
        Assert.Equal(nameof(StationOperationStatus.RecoveryRequired), plan.UnresolvedLoad.Status);
    }

    // ------------------------------------------------------------------------------------------------
    // agv01 那一形：开往取货站途中车载端离线，没有任何仓位操作。
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AJourneyOnItsWayToThePickupWithTheVehicleOfflineIsWithdrawnWithoutSlotFacts()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow onItsWay = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, onItsWay.Stage);
        await fixture.DropOnboardSessionAsync();
        JourneyWithdrawalRequest request = Request(onItsWay, slotFacts: null, cargoLocation: null);

        JourneyWithdrawalResult withdrawn = await PlanAndWithdrawAsync(fixture, request);

        Assert.True(withdrawn.Outcome == "WITHDRAWN", string.Join(",", withdrawn.Codes));
        Assert.Equal(JourneyRuntimeStage.Completed, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        Assert.Contains(
            FirstDemandId,
            await DemandJourneyLookup.ReleasedForRedispatch(fixture.Context).AsNoTracking().Select(row => row.DemandId).ToArrayAsync(Token));
    }

    // ------------------------------------------------------------------------------------------------
    // 重放与过期的确认码。
    // ------------------------------------------------------------------------------------------------

    /// <summary>同一个确认码按两次只执行一次：第二次 <c>ALREADY_DONE</c>，什么都不再写（审计照写一条）。</summary>
    [Fact]
    public async Task TheSameWithdrawalPressedTwiceRunsOnce()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        JourneyWithdrawalRequest request = await RequestAsync(fixture, blocked);
        JourneyWithdrawalResult planned = await JourneyWithdrawal.PlanAsync(fixture.Context, request, Token);

        JourneyWithdrawalResult first = await WithdrawAsync(fixture, request with { ConfirmationCode = planned.ConfirmationCode });
        int membershipsAfterFirst = await fixture.Context.Set<JourneyDemandRow>().CountAsync(Token);
        JourneyWithdrawalResult second = await WithdrawAsync(fixture, request with { ConfirmationCode = planned.ConfirmationCode });

        Assert.Equal(("WITHDRAWN", "ALREADY_DONE"), (first.Outcome, second.Outcome));
        Assert.Equal(membershipsAfterFirst, await fixture.Context.Set<JourneyDemandRow>().CountAsync(Token));
        Assert.Equal(
            [DemandJourneyLookup.ReleasedForRedispatchReason, DemandJourneyLookup.ReleasedForRedispatchReason],
            await fixture.Context.Set<JourneyDemandRow>().AsNoTracking().Select(row => row.RemovalReason ?? "").ToArrayAsync(Token));
    }

    /// <summary>
    /// 演练之后状态变了（这里是车掉线，会话的原因码跟着变）：确认码对不上，<c>CONFIRMATION_STALE</c>，一行不写，审计记一次失败，
    /// 结果里给出新的计划与新码，让人重新看过再按。
    /// </summary>
    [Fact]
    public async Task AConfirmationCodeFromBeforeTheStateChangedIsRefused()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        JourneyWithdrawalRequest request = await RequestAsync(fixture, blocked);
        JourneyWithdrawalResult planned = await JourneyWithdrawal.PlanAsync(fixture.Context, request, Token);
        await fixture.DropOnboardSessionAsync();

        JourneyWithdrawalResult refused = await WithdrawAsync(fixture, request with { ConfirmationCode = planned.ConfirmationCode });

        Assert.Equal("REJECTED", refused.Outcome);
        Assert.Equal([JourneyWithdrawal.ConfirmationStale], refused.Codes);
        Assert.NotEqual(planned.ConfirmationCode, refused.ConfirmationCode);
        await AssertNothingWithdrawnAsync(fixture);
        Assert.Equal(
            GovernanceActionOutcome.Failed,
            (await fixture.Context.Set<AdministratorAuditRecordRow>().AsNoTracking()
                .SingleAsync(row => row.Action == JourneyWithdrawal.AuditAction, Token)).Outcome);
    }

    [Fact]
    public async Task AWithdrawalWithoutAConfirmationCodeIsRefused()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);

        JourneyWithdrawalResult refused = await WithdrawAsync(fixture, await RequestAsync(fixture, blocked));

        Assert.Equal("REJECTED", refused.Outcome);
        Assert.Equal([JourneyWithdrawal.ConfirmationRequired], refused.Codes);
        await AssertNothingWithdrawnAsync(fixture);
    }

    // ------------------------------------------------------------------------------------------------
    // 拒绝：车上可能有货（D2：真有货就拒绝撤下，改走车载端的恢复会话）。
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASiteReportOfCargoInTheSlotRefusesTheWithdrawal()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        int[] slots = await TargetSlotsAsync(fixture, FirstDemandId);
        JourneyWithdrawalRequest request = Request(
            blocked, string.Join(",", slots.Select(slot => $"{slot}={JourneyWithdrawal.SlotOccupied}")), CargoLocation);

        await AssertRefusedEverywhereAsync(fixture, request, JourneyWithdrawal.SiteReportsCargo);
    }

    /// <summary>车报来的那次结果里那个仓有货：现场说空也不信，拒绝——两者对不上就是要人在车上处理的事。</summary>
    [Fact]
    public async Task AVehicleReportOfCargoInTheSlotRefusesTheWithdrawalWhateverTheSiteSays()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture, reported: SlotBusinessState.Occupied);

        await AssertRefusedEverywhereAsync(fixture, await RequestAsync(fixture, blocked), JourneyWithdrawal.SlotEvidenceNotEmpty);
    }

    [Fact]
    public async Task AVehicleReportOfAnUnknownSlotStateRefusesTheWithdrawal()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture, reported: SlotBusinessState.Unknown);

        await AssertRefusedEverywhereAsync(fixture, await RequestAsync(fixture, blocked), JourneyWithdrawal.SlotEvidenceNotEmpty);
    }

    /// <summary>旅程上已经装上一篮：撤下会让车上的货没有需求认领，拒绝。</summary>
    [Fact]
    public async Task ALoadedDemandOnTheJourneyRefusesTheWithdrawal()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow runtime = await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await fixture.ApplySafeResultAsync(
            await OperationOfAsync(fixture, FirstDemandId), SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);
        await EnterSublotAsync(fixture, FirstDemandId, SecondSublot, SecondSubmissionId);
        await fixture.ApplyTimedOutResultAsync(await OperationOfAsync(fixture, SecondDemandId), SlotOperationType.Load);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow blocked = await JourneyOfAsync(fixture, SecondDemandId);
        Assert.Equal(JourneyRuntimeStage.Blocked, blocked.Stage);
        int[] slots = await TargetSlotsAsync(fixture, SecondDemandId);

        await AssertRefusedEverywhereAsync(
            fixture, Request(blocked, EmptyLocked(slots), CargoLocation), JourneyWithdrawal.CargoOnBoard);
        _ = runtime;
    }

    // ------------------------------------------------------------------------------------------------
    // 拒绝：仓位事实不全。
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnUnresolvedLoadNeedsTheSlotFactsAndWhereTheBasketIs()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);

        JourneyWithdrawalResult refused = await JourneyWithdrawal.PlanAsync(
            fixture.Context, Request(blocked, slotFacts: null, cargoLocation: null), Token);

        Assert.Equal("REJECTED", refused.Outcome);
        Assert.Equal(
            [JourneyWithdrawal.CargoLocationRequired, JourneyWithdrawal.SlotFactsRequired],
            refused.Codes.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("99=EMPTY_LOCKED")]
    [InlineData("EMPTY_LOCKED")]
    [InlineData("5=EMPTY")]
    public async Task SlotFactsThatDoNotCoverTheTargetSlotsAreRefused(string facts)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);

        JourneyWithdrawalResult refused = await JourneyWithdrawal.PlanAsync(fixture.Context, Request(blocked, facts, CargoLocation), Token);

        Assert.Equal("REJECTED", refused.Outcome);
        Assert.Equal([JourneyWithdrawal.SlotFactsMalformed], refused.Codes);
    }

    /// <summary>
    /// 审查 S-5：格式对、仓号在 1..8、写的也是空且锁着，只是两个目标仓位只写了一个（或多写了一个）：只拒在「盖不住目标仓位」上，不是格式错。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WellFormedSlotFactsThatNameOneTargetSlotOnlyOrOneTooManyAreRefusedAsAMismatch(bool oneTooMany)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture, boxCount: 8);
        int[] targets = await TargetSlotsAsync(fixture, FirstDemandId);
        Assert.Equal(2, targets.Length);
        int[] named = oneTooMany
            ? [.. targets, Enumerable.Range(1, 8).First(slot => !targets.Contains(slot))]
            : [targets[0]];

        JourneyWithdrawalResult refused = await JourneyWithdrawal.PlanAsync(
            fixture.Context, Request(blocked, EmptyLocked(named), CargoLocation), Token);

        Assert.Equal("REJECTED", refused.Outcome);
        Assert.Equal([JourneyWithdrawal.SlotFactsMismatch], refused.Codes);
        JourneyWithdrawalResult covered = await JourneyWithdrawal.PlanAsync(
            fixture.Context, Request(blocked, EmptyLocked(targets), CargoLocation), Token);
        Assert.True(covered.Outcome == "PLANNED", string.Join(",", covered.Codes));
    }

    /// <summary>车最近报的安全摘要说仓门读不出、没锁或开锁输出没复位，或者车从没报过：门没证明关着，拒绝。</summary>
    [Theory]
    [InlineData("LOCK_NOT_CLOSED")]
    [InlineData("SLOT_STATE_UNKNOWN")]
    [InlineData("UNLOCK_OUTPUT_NOT_RESET")]
    [InlineData(null)]
    public async Task ADoorTheVehicleDoesNotProveClosedRefusesTheWithdrawal(string? reason)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        await fixture.SetSafetyEvidenceAsync(unknownPresent: reason is not "LOCK_NOT_CLOSED", reasonCodes: reason is null ? null : [reason]);
        fixture.Context.ChangeTracker.Clear();

        await AssertRefusedEverywhereAsync(fixture, await RequestAsync(fixture, blocked), JourneyWithdrawal.DoorNotProvenClosed);
    }

    /// <summary>
    /// 车只是没停稳或运动状态读不到（agv03 10-19 被人开走之后就是这样）：那是现场顺序里的另一步，不是门的事，不挡撤下。
    /// </summary>
    [Fact]
    public async Task AVehicleNotYetStoppedDoesNotRefuseTheWithdrawal()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        await fixture.SetSafetyEvidenceAsync(unknownPresent: true, reasonCodes: ["VEHICLE_NOT_READY", "ACTION_NOT_ALLOWED_IN_STATE"]);
        fixture.Context.ChangeTracker.Clear();

        JourneyWithdrawalResult planned = await JourneyWithdrawal.PlanAsync(fixture.Context, await RequestAsync(fixture, blocked), Token);

        Assert.True(planned.Outcome == "PLANNED", string.Join(",", planned.Codes));
    }

    // ------------------------------------------------------------------------------------------------
    // 拒绝：必填项、对象不符。
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryMissingFieldIsNamed()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        JourneyWithdrawalRequest request = await RequestAsync(fixture, blocked) with
        {
            OperatorId = " ",
            Reason = null,
            SiteVerification = "",
            RiotVerification = null
        };

        JourneyWithdrawalResult refused = await JourneyWithdrawal.PlanAsync(fixture.Context, request, Token);

        Assert.Equal(
            [
                JourneyWithdrawal.OperatorRequired, JourneyWithdrawal.ReasonRequired, JourneyWithdrawal.RiotVerificationRequired,
                JourneyWithdrawal.SiteVerificationRequired
            ],
            refused.Codes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnotherVehiclesKeyIsRefused()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);

        await AssertRefusedEverywhereAsync(
            fixture, await RequestAsync(fixture, blocked) with { VehicleKey = "BROKERX-another-vehicle" }, JourneyWithdrawal.VehicleMismatch);
    }

    [Fact]
    public async Task AnUnknownJourneyIsRefused()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);

        JourneyWithdrawalResult refused = await JourneyWithdrawal.PlanAsync(
            fixture.Context, await RequestAsync(fixture, blocked) with { JourneyId = "journey:nobody" }, Token);

        Assert.Equal("REJECTED", refused.Outcome);
        Assert.Equal([JourneyWithdrawal.JourneyNotFound], refused.Codes);
    }

    // ------------------------------------------------------------------------------------------------
    // 拒绝：订单、恢复会话、故障、急停（D6：第一版遇到活单拒绝）。
    // ------------------------------------------------------------------------------------------------

    /// <summary>这一趟有一张建单结果未知的单：RIoT 上可能有一张活单，而且它会永远挡住这辆车的空闲返回，拒绝。</summary>
    [Fact]
    public async Task AnOrderWhoseCreateOutcomeIsUnknownRefusesTheWithdrawal()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        OrderIntentRow intent = await fixture.Context.OrderIntents.SingleAsync(row => row.UpperId == blocked.PickupUpperId, Token);
        intent.Status = "RESULT_UNKNOWN";
        intent.OrderId = null;
        intent.CreateAttemptCount = 1;
        intent.CreateAttemptId = "attempt-771";
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        await AssertRefusedEverywhereAsync(fixture, await RequestAsync(fixture, blocked), JourneyWithdrawal.OrderOutcomeUnknown);
    }

    [Fact]
    public async Task AnOpenExceptionRecoverySessionRefusesTheWithdrawal()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        fixture.Context.ExceptionRecoverySessions.Add(new ExceptionRecoverySessionRow
        {
            ExceptionRecoverySessionId = "ers-771",
            RequestId = "req-771",
            RequestContentHash = new string('1', 64),
            AgvId = fixture.Options.AgvId,
            EventId = "evt-771",
            DemandId = FirstDemandId,
            SlotsJson = "[]",
            AdministratorId = "ADMIN",
            AdministratorRole = "ROLE",
            Reason = "open",
            State = "OPEN",
            OpenedAt = Now,
            UpdatedAt = Now
        });
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        await AssertRefusedEverywhereAsync(fixture, await RequestAsync(fixture, blocked), JourneyWithdrawal.RecoverySessionOpen);
    }

    [Fact]
    public async Task AFaultInEffectRefusesTheWithdrawal()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        await new VehicleFaultStore(fixture.Context).RecordLevelAsync(
            fixture.Options.AgvId, VehicleFaultLevel.SuspectedBlocked, "TEST_771", false, Now, Token);
        fixture.Context.ChangeTracker.Clear();

        await AssertRefusedEverywhereAsync(fixture, await RequestAsync(fixture, blocked), JourneyWithdrawal.FaultInEffect);
    }

    // ------------------------------------------------------------------------------------------------
    // 审查 M-1：建单结果未知的意图，豁免只到空闲返回评估器自己的豁免为止。
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 审查 M-1 探针 P1：RESULT_UNKNOWN、从没发出建单（建单前那次读什么也没读到）的那一形。<see cref="WireToGateStore.IsNeverSentAsync"/> 说它没发出，
    /// 但空闲返回评估器只豁免「判作没建成」的那一种，不豁免它：旅程一收尾，它就永远挡着这辆车的空闲返回，谁也不再去结它。所以撤下拒。
    /// </summary>
    [Fact]
    public async Task AnOrderNeverSentButStillResultUnknownRefusesTheWithdrawal()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        // Moved onto a leg of its own, so the audit of the real create stays out of it: one pre-create read that answered nothing.
        OrderIntentRow intent = await fixture.Context.OrderIntents.AsNoTracking().SingleAsync(row => row.UpperId == blocked.PickupUpperId, Token);
        await fixture.Context.OrderIntents.Where(row => row.MovementLegId == intent.MovementLegId).ExecuteDeleteAsync(Token);
        intent.MovementLegId = "leg-771-never-sent";
        intent.Status = "RESULT_UNKNOWN";
        intent.OrderId = null;
        intent.CreateAttemptCount = 0;
        intent.CreateAttemptId = null;
        intent.DispatchAuditVersion = 1;
        intent.ExperimentalCreateAuthorizationId = null;
        fixture.Context.OrderIntents.Add(intent);
        fixture.Context.RiotDispatchAuditEvents.Add(new RiotDispatchAuditEventRow
        {
            AuditEventId = "audit-771-never-sent",
            MovementLegId = intent.MovementLegId,
            UpperId = intent.UpperId,
            Sequence = 1,
            Phase = "PRE_CREATE_RECONCILIATION",
            Outcome = "UNKNOWN",
            OccurredAt = Now,
        });
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        OrderIntentRow stored = await fixture.Context.OrderIntents.AsNoTracking().SingleAsync(row => row.UpperId == blocked.PickupUpperId, Token);
        Assert.True(await new WireToGateStore(fixture.Context).IsNeverSentAsync(stored, Token));
        Assert.False(await AbandonedDemandLegOrders.TakenAsNeverCreatedAsync(fixture.Context, stored, Token));

        await AssertRefusedEverywhereAsync(fixture, await RequestAsync(fixture, blocked), JourneyWithdrawal.OrderOutcomeUnknown);
    }

    // ------------------------------------------------------------------------------------------------
    // 审查 S-2：只从三种状态撤下。
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 审查 S-2 探针 P2：阻塞码不是「装货结果要恢复」的都拒——卸货结果要恢复（车上有货）、恢复结果没对上的那一族、结束需求上没对上的（不可放行，
    /// 出口在 control-server#485）。不在取货途中、也没有掉线码的未阻塞旅程同样拒：那是引擎在跑的。
    /// </summary>
    [Theory]
    [InlineData(nameof(JourneyRuntimeStage.Blocked), "UNLOAD_RESULT_REQUIRES_RECOVERY")]
    [InlineData(nameof(JourneyRuntimeStage.Blocked), "LOAD_RESULT_NOT_RECONCILED")]
    [InlineData(nameof(JourneyRuntimeStage.Blocked), "LOAD_RESULT_NOT_RECONCILED_ON_ENDED_DEMAND")]
    [InlineData(nameof(JourneyRuntimeStage.Blocked), "LOAD_RESULT_NOT_RECONCILED_BEFORE_UPGRADE")]
    [InlineData(nameof(JourneyRuntimeStage.AwaitingLoadResult), null)]
    public async Task AJourneyInAnyOtherStateIsRefused(string stage, string? blockReason)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        await SetStateAsync(fixture, blocked, Enum.Parse<JourneyRuntimeStage>(stage), blockReason);

        await AssertRefusedEverywhereAsync(fixture, await RequestAsync(fixture, blocked), JourneyWithdrawal.JourneyStateNotWithdrawable);
    }

    /// <summary>白名单的另一头：车载端会话掉线或没就绪时等装货结果的旅程可以撤（掉线就等不来结果）。</summary>
    [Theory]
    [InlineData(JourneyRuntimeEngine.OnboardSessionLostReason)]
    [InlineData("ONBOARD_SESSION_NOT_READY")]
    public async Task AJourneyWaitingOnALostOrNotReadySessionMayBeWithdrawn(string blockReason)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        await SetStateAsync(fixture, blocked, JourneyRuntimeStage.AwaitingLoadResult, blockReason);

        JourneyWithdrawalResult planned = await JourneyWithdrawal.PlanAsync(fixture.Context, await RequestAsync(fixture, blocked), Token);

        Assert.True(planned.Outcome == "PLANNED", string.Join(",", planned.Codes));
    }

    /// <summary>
    /// 审查 S-2：要恢复的只能是这一次装货那一条需求。另一条也标着要恢复，是一个没人下结论的恢复结果，放回待派等于把它埋了，拒。
    /// </summary>
    [Fact]
    public async Task AnotherDemandMarkedForRecoveryRefusesTheWithdrawal()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        await fixture.Context.AcceptedDemands
            .Where(row => row.DemandId == SecondDemandId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, DemandExecutionStatus.RecoveryRequired), Token);
        fixture.Context.ChangeTracker.Clear();

        await AssertRefusedEverywhereAsync(fixture, await RequestAsync(fixture, blocked), JourneyWithdrawal.OtherDemandRecoveryRequired);
    }

    // ------------------------------------------------------------------------------------------------
    // 审查 S-5：此前没有用例的拒绝。
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnOpenEmergencyStopRefusesTheWithdrawal()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        await new RiotOrderCommandAuditStore(fixture.Context).ArmAttemptAsync(
            RiotCommandTypeNames.TriggerEmergency,
            fixture.Options.AgvId,
            "vehicle:" + blocked.VehicleKey,
            null,
            new string('7', 64),
            1,
            fixture.Clock.GetUtcNow(),
            Token);
        fixture.Context.ChangeTracker.Clear();

        await AssertRefusedEverywhereAsync(fixture, await RequestAsync(fixture, blocked), JourneyWithdrawal.EmergencyStopOpen);
    }

    // ------------------------------------------------------------------------------------------------
    // 重建记录（control-server#770 合入之后）：待发、停住的随撤下了结，其余仍拒。
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// agv01 那一形（10-18）：车开往取货站途中，单在 RIoT 被取消，服务端记下一条待发的重建；车随后掉线。撤下把这条重建转成
    /// <c>ENDED</c>、记下操作人，需求退回待派；之后延迟过了，引擎也不再为这一趟建新单。
    /// </summary>
    [Fact]
    public async Task APendingRebuildOfAJourneyOnItsWayToThePickupIsEndedByTheWithdrawal()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow onItsWay = await JourneyOfAsync(fixture, FirstDemandId);
        fixture.Riot.CancelOrder(onItsWay.PickupUpperId);
        await TickAndRunAsync(fixture);
        OwnOrderRebuildRow pending = await fixture.Context.OwnOrderRebuilds.AsNoTracking().SingleAsync(Token);
        Assert.Equal(OwnOrderRebuildStates.Pending, pending.State);
        await fixture.DropOnboardSessionAsync();
        fixture.Context.ChangeTracker.Clear();
        JourneyWithdrawalRequest request = Request(await JourneyOfAsync(fixture, FirstDemandId), slotFacts: null, cargoLocation: null);

        JourneyWithdrawalResult planned = await JourneyWithdrawal.PlanAsync(fixture.Context, request, Token);
        Assert.True(planned.Outcome == "PLANNED", string.Join(",", planned.Codes));
        Assert.Equal(
            [(pending.RebuildId, OwnOrderRebuildStates.Pending)],
            planned.Plan!.Rebuilds!.Select(item => (item.RebuildId, item.State)));
        await PlanAndWithdrawAsync(fixture, request);

        OwnOrderRebuildRow ended = await fixture.Context.OwnOrderRebuilds.AsNoTracking().SingleAsync(Token);
        Assert.Equal((OwnOrderRebuildStates.Ended, Operator), (ended.State, ended.OperatorId));
        Assert.Contains(
            FirstDemandId,
            await DemandJourneyLookup.ReleasedForRedispatch(fixture.Context).AsNoTracking().Select(row => row.DemandId).ToArrayAsync(Token));
        // The released demand may be dispatched again under a journey of its own; the rebuild's new order never is.
        fixture.Clock.Advance(fixture.Options.OwnOrderRebuildDelay);
        Assert.Null(await UnloadCrashResumeTests.RunRoundAsync(fixture));
        fixture.Context.ChangeTracker.Clear();
        Assert.False(await fixture.Context.OrderIntents.AsNoTracking().AnyAsync(row => row.UpperId == pending.NewUpperId, Token));
        Assert.Equal(OwnOrderRebuildStates.Ended, (await fixture.Context.OwnOrderRebuilds.AsNoTracking().SingleAsync(Token)).State);
    }

    /// <summary>待发的、停住等人的（含 control-server#770 的强制充电停住）都随撤下转 <c>ENDED</c>，记下操作人。</summary>
    [Theory]
    [InlineData(OwnOrderRebuildStates.Pending, null)]
    [InlineData(OwnOrderRebuildStates.Stopped, "VEHICLE_NO_LONGER_ELIGIBLE")]
    [InlineData(OwnOrderRebuildStates.Stopped, "MANDATORY_CHARGE_REQUIRED")]
    public async Task ARebuildNotYetOrderedIsEndedByTheWithdrawal(string state, string? stoppedReason)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        await AddRebuildAsync(fixture, blocked, state, stoppedReason);

        await PlanAndWithdrawAsync(fixture, await RequestAsync(fixture, blocked));

        OwnOrderRebuildRow ended = await fixture.Context.OwnOrderRebuilds.AsNoTracking().SingleAsync(Token);
        Assert.Equal((OwnOrderRebuildStates.Ended, Operator), (ended.State, ended.OperatorId));
    }

    /// <summary>
    /// 新单的意图已写下、正在建（<c>ORDERING</c>），新单确认前就 FAILED 或终结了、还没人接手，或者已转车载端交接货物：RIoT 上可能有一张单，
    /// 或者另一条出口正在管它。这一版不碰，仍以 <c>REBUILD_RECORD_OPEN</c> 拒。
    /// </summary>
    [Theory]
    [InlineData(OwnOrderRebuildStates.Ordering, null)]
    [InlineData(OwnOrderRebuildStates.Failed, null)]
    [InlineData(OwnOrderRebuildStates.AwaitingCargoHandoff, null)]
    [InlineData(OwnOrderRebuildStates.Stopped, "REBUILT_ORDER_ENDED_BEFORE_CONFIRMATION")]
    public async Task ARebuildThatMayHaveAnOrderOrAnotherExitRefusesTheWithdrawal(string state, string? stoppedReason)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        await AddRebuildAsync(fixture, blocked, state, stoppedReason);

        await AssertRefusedEverywhereAsync(fixture, await RequestAsync(fixture, blocked), JourneyWithdrawal.RebuildRecordOpen);
        Assert.Equal(state, (await fixture.Context.OwnOrderRebuilds.AsNoTracking().SingleAsync(Token)).State);
    }

    private static async Task AddRebuildAsync(RuntimeFixture fixture, JourneyRuntimeRow journey, string state, string? stoppedReason)
    {
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        fixture.Context.OwnOrderRebuilds.Add(new OwnOrderRebuildRow
        {
            RebuildId = "rebuild-771",
            JourneyId = journey.JourneyId,
            DemandId = FirstDemandId,
            AgvId = fixture.Options.AgvId,
            VehicleKey = journey.VehicleKey,
            StopId = journey.JourneyId + "|PICKUP",
            Source = OwnOrderRebuildSources.OrderNeverAppeared,
            EndedUpperId = journey.PickupUpperId,
            NewUpperId = journey.PickupUpperId + "-R1",
            NewMovementLegId = "rebuild-771-leg",
            IncidentAt = now,
            RecordedAt = now,
            DueAt = now,
            State = state,
            StoppedReason = stoppedReason,
            StoppedAt = stoppedReason is null ? null : now,
        });
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>那一次装货车从没回过结果（命令发出后车再没回话）：没有车的读数，现场说空也不信，拒。</summary>
    [Fact]
    public async Task AnUnresolvedLoadTheVehicleNeverReportedOnRefusesTheWithdrawal()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        string attempt = (await OperationOfAsync(fixture, FirstDemandId)).SlotOperationAttemptId;
        await fixture.Context.OperationResults
            .Where(row => row.SlotOperationAttemptId == attempt)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.HistoricalOnly, true), Token);
        fixture.Context.ChangeTracker.Clear();

        await AssertRefusedEverywhereAsync(fixture, await RequestAsync(fixture, blocked), JourneyWithdrawal.SlotEvidenceMissing);
    }

    /// <summary>没结的不是一次装货（这里把它改成卸货）：这一版只收拾一次装货，别的要人在车上处理，拒。</summary>
    [Fact]
    public async Task AnUnresolvedOperationOtherThanOneLoadRefusesTheWithdrawal()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        int[] slots = await TargetSlotsAsync(fixture, FirstDemandId);
        string attempt = (await OperationOfAsync(fixture, FirstDemandId)).SlotOperationAttemptId;
        await fixture.Context.StationOperations
            .Where(row => row.SlotOperationAttemptId == attempt)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.OperationType, SlotOperationType.Unload), Token);
        fixture.Context.ChangeTracker.Clear();

        await AssertRefusedEverywhereAsync(
            fixture, Request(blocked, EmptyLocked(slots), CargoLocation), JourneyWithdrawal.OtherOperationUnresolved);
    }

    /// <summary>充电旅程不带需求：没有可放回待派的东西，这个出口不收，拒。</summary>
    [Fact]
    public async Task AJourneyCarryingNoDemandIsRefused()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow charging = ControlServer.Host.Runtime.Charging.ChargingJourneyShape.Build(
            ChargingIdentity.JourneyIdPrefix + fixture.Options.VehicleKey + ":771",
            new ControlServer.Host.Runtime.Fleet.FleetVehicle(fixture.Options.AgvId, fixture.Options.VehicleKey, 1),
            new ChargerRosterEntry(fixture.Options.MapId, 771, "CHG-771", null, null, []),
            1,
            "MANDATORY_CHARGE",
            fixture.Options,
            fixture.Clock.GetUtcNow()).Runtime;
        fixture.Context.JourneyRuntimes.Add(charging);
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        await AssertRefusedEverywhereAsync(fixture, Request(charging, null, null), JourneyWithdrawal.JourneyKindNotWithdrawable);
    }

    // ------------------------------------------------------------------------------------------------
    // 审查 S-4：计划里给出判门所依据的会话事实。
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 计划列出会话代次、安全摘要的修订号、它的原因码、车观测它的时刻与会话最后更新的时刻：现场据此判断「门关着」那一条摘要是哪一刻的。
    /// </summary>
    [Fact]
    public async Task ThePlanShowsTheSessionFactsTheDoorsAreJudgedOn()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(Token);

        JourneyWithdrawalPlan plan = (await JourneyWithdrawal.PlanAsync(fixture.Context, await RequestAsync(fixture, blocked), Token)).Plan!;

        Assert.Equal(session.SessionGeneration, plan.SessionGeneration);
        Assert.NotNull(plan.SessionSafetyRevision);
        Assert.Equal(session.SafetyRevision, plan.SessionSafetyRevision);
        Assert.NotNull(plan.SessionSafetyReasonCodes);
        Assert.Equal(session.UpdatedAt, plan.SessionUpdatedAt);
        Assert.NotNull(plan.SessionSafetySummaryObservedAt);
    }

    // ------------------------------------------------------------------------------------------------
    // 审查 S-3：撤下之后那趟旅程名下的单若在 RIoT 上跑起来，外来单监督器挡车、不取消。
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 撤下前现场要核过 RIoT 上本车没有未结束的单；核错了、或撤下之后那张取货单又在本车上跑起来时，它仍对得上一条订单意图，原本会被当成本服务端
    /// 自己的在途单、没人管。现在依据 <c>OWN_JOURNEY_WITHDRAWN_BY_OPERATOR</c> 记成「归属证明不了」：挡车、告警，不发任何订单命令。
    /// </summary>
    [Fact]
    public async Task AnOrderOfAWithdrawnJourneyRunningAgainHoldsTheVehicleAndIsNotCancelled()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        await PlanAndWithdrawAsync(fixture, await RequestAsync(fixture, blocked));
        int commandsBefore = fixture.Riot.OrderCommands.Count;

        fixture.Riot.SetOrderState(blocked.PickupUpperId, 3, terminal: false);
        string orderId = fixture.Riot.OrderIdOf(blocked.PickupUpperId)!;
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        ForeignRiotOrderRow held = await fixture.Context.ForeignRiotOrders.AsNoTracking().SingleAsync(row => row.RiotOrderId == orderId, Token);
        Assert.Equal(
            (ForeignRiotOrderOwnership.Unproven, WithdrawnJourneyOrders.OwnershipBasis),
            (held.Ownership, held.OwnershipBasis));
        Assert.Contains(fixture.Options.AgvId, await ForeignRunningOrders.HeldAgvIdsAsync(fixture.Context, Token));
        Assert.Equal(commandsBefore, fixture.Riot.OrderCommands.Count);
    }

    // ------------------------------------------------------------------------------------------------
    // 撤下之后：引擎照常推进，不因这趟旅程抛。
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheEngineRunsOnAfterAWithdrawal()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        await PlanAndWithdrawAsync(fixture, await RequestAsync(fixture, blocked));

        Exception? round = await UnloadCrashResumeTests.RunRoundAsync(fixture);

        Assert.Null(round);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == blocked.JourneyId, Token)).Stage);
    }

    // ------------------------------------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// agv03 那一形：一个取货站两条，第一条录入、下命令，车报一次失败且仓位读数为 <paramref name="reported"/>，结果进恢复、旅程阻塞；
    /// 第二条还没装。
    /// </summary>
    internal static async Task<JourneyRuntimeRow> BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(
        RuntimeFixture fixture, SlotBusinessState reported = SlotBusinessState.Empty, int boxCount = 4)
    {
        await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture, boxCount);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        StationOperationRow operation = await OperationOfAsync(fixture, FirstDemandId);
        if (reported == SlotBusinessState.Empty)
        {
            await fixture.ApplyTimedOutResultAsync(operation, SlotOperationType.Load);
        }
        else
        {
            int[] slots = JsonSerializer.Deserialize<int[]>(operation.TargetSlotsJson) ?? [];
            await new WireToGateStore(fixture.Context).ApplyOperationResultAsync(
                new StationOperationResult(
                    Guid.NewGuid().ToString("D"),
                    operation.SlotOperationAttemptId,
                    operation.DemandId,
                    SlotOperationType.Load,
                    "UNKNOWN",
                    slots.Select(slot => new SlotPhysicalEvidence(slot, reported, true, true)).ToArray(),
                    false,
                    fixture.Clock.GetUtcNow(),
                    new string('8', 64),
                    new string('9', 64)),
                fixture.Options.AgvId,
                0,
                Token);
        }
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow blocked = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal((JourneyRuntimeStage.Blocked, "LOAD_RESULT_REQUIRES_RECOVERY"), (blocked.Stage, blocked.BlockReasonCode));
        Assert.Equal(JourneyDemandStatuses.PendingLoad, (await Batch7MultiDemandAdvanceTests.MembershipAsync(fixture, SecondDemandId)).Status);
        // agv03 after the timeout: the lock read closed again (14:37:07.00), so its safety summary names no door.
        await fixture.ProveSlotDoorsClosedAsync();
        return blocked;
    }

    internal static async Task<JourneyWithdrawalRequest> RequestAsync(RuntimeFixture fixture, JourneyRuntimeRow journey) =>
        Request(journey, EmptyLocked(await TargetSlotsAsync(fixture, FirstDemandId)), CargoLocation);

    internal static JourneyWithdrawalRequest Request(JourneyRuntimeRow journey, string? slotFacts, string? cargoLocation) =>
        new(journey.JourneyId, journey.VehicleKey, Operator, Reason, SiteVerification, RiotVerification, slotFacts, cargoLocation);

    internal static string EmptyLocked(int[] slots) =>
        string.Join(",", slots.Select(slot => $"{slot}={JourneyWithdrawal.SlotEmptyLocked}"));

    internal static async Task<int[]> TargetSlotsAsync(RuntimeFixture fixture, string demandId) =>
        JsonSerializer.Deserialize<int[]>((await OperationOfAsync(fixture, demandId)).TargetSlotsJson) ?? [];

    /// <summary>Puts the journey into <paramref name="stage"/> with <paramref name="blockReason"/>, the rest of its state as it was.</summary>
    private static async Task SetStateAsync(RuntimeFixture fixture, JourneyRuntimeRow journey, JourneyRuntimeStage stage, string? blockReason)
    {
        JourneyRuntimeRow row = await fixture.Context.JourneyRuntimes.SingleAsync(item => item.JourneyId == journey.JourneyId, Token);
        row.Stage = stage;
        row.SetBlockReason(blockReason, fixture.Clock.GetUtcNow());
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    private static Task<StationOperationRow> OperationOfAsync(RuntimeFixture fixture, string demandId) =>
        fixture.Context.StationOperations.AsNoTracking()
            .SingleAsync(row => row.DemandId == demandId && row.OperationType == SlotOperationType.Load, Token);

    private static Task<JourneyWithdrawalResult> WithdrawAsync(RuntimeFixture fixture, JourneyWithdrawalRequest request) =>
        JourneyWithdrawal.WithdrawAsync(fixture.Context, Audit(fixture), request, fixture.Clock.GetUtcNow(), publisher: null, Token);

    internal static async Task<JourneyWithdrawalResult> PlanAndWithdrawAsync(RuntimeFixture fixture, JourneyWithdrawalRequest request)
    {
        JourneyWithdrawalResult planned = await JourneyWithdrawal.PlanAsync(fixture.Context, request, Token);
        Assert.True(planned.Outcome == "PLANNED", string.Join(",", planned.Codes));
        JourneyWithdrawalResult withdrawn = await WithdrawAsync(fixture, request with { ConfirmationCode = planned.ConfirmationCode });
        Assert.True(withdrawn.Outcome == "WITHDRAWN", string.Join(",", withdrawn.Codes));
        fixture.Context.ChangeTracker.Clear();
        return withdrawn;
    }

    private static GovernanceStore Audit(RuntimeFixture fixture) =>
        new(fixture.Context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default);

    /// <summary>演练拒绝、撤下也拒绝（带着随便一个确认码），一行不写；撤下那一次写一条失败的审计。</summary>
    private static async Task AssertRefusedEverywhereAsync(RuntimeFixture fixture, JourneyWithdrawalRequest request, string code)
    {
        JourneyWithdrawalResult planned = await JourneyWithdrawal.PlanAsync(fixture.Context, request, Token);
        Assert.True(
            planned.Outcome == "REJECTED" && planned.Codes.Contains(code),
            $"plan: {planned.Outcome} [{string.Join(",", planned.Codes)}], expected {code}");
        Assert.Null(planned.ConfirmationCode);

        JourneyWithdrawalResult withdrawn = await WithdrawAsync(fixture, request with { ConfirmationCode = "ANY-CODE" });
        Assert.True(
            withdrawn.Outcome == "REJECTED" && withdrawn.Codes.Contains(code),
            $"withdraw: {withdrawn.Outcome} [{string.Join(",", withdrawn.Codes)}], expected {code}");
        fixture.Context.ChangeTracker.Clear();
        await AssertNothingWithdrawnAsync(fixture);
        Assert.Equal(
            GovernanceActionOutcome.Failed,
            (await fixture.Context.Set<AdministratorAuditRecordRow>().AsNoTracking()
                .SingleAsync(row => row.Action == JourneyWithdrawal.AuditAction, Token)).Outcome);
    }

    private static async Task AssertNothingWithdrawnAsync(RuntimeFixture fixture)
    {
        fixture.Context.ChangeTracker.Clear();
        Assert.Empty(await fixture.Context.JourneyRuntimes.AsNoTracking()
            .Where(row => row.BlockReasonCode == JourneyWithdrawal.WithdrawnReason).ToArrayAsync(Token));
        Assert.Empty(await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .Where(row => row.RemovedAt != null).ToArrayAsync(Token));
    }
}
