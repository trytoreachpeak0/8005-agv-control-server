using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
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

    /// <summary>车要收的收尾快照落进发件箱，空清单说「本站因任务释放结束」：这批会重新派车，不是丢了。</summary>
    [Fact]
    public async Task TheVehicleIsToldTheStopEndedBecauseTheDemandsWereReleased()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);

        await PlanAndWithdrawAsync(fixture, await RequestAsync(fixture, blocked));

        JsonElement worklist = await fixture.OutboxPayloadAsync(JourneyClosure.SnapshotMessageIds(blocked.JourneyId)[0]);
        Assert.Equal("DEMAND_RELEASED", worklist.GetProperty("payload").GetProperty("stopEndedReason").GetString());
        Assert.Equal(0, worklist.GetProperty("payload").GetProperty("items").GetArrayLength());
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

        Assert.Equal(("REJECTED", [JourneyWithdrawal.ConfirmationStale]), (refused.Outcome, refused.Codes.ToArray()));
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

        Assert.Equal(("REJECTED", [JourneyWithdrawal.ConfirmationRequired]), (refused.Outcome, refused.Codes.ToArray()));
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
        Assert.Contains(
            refused.Codes,
            code => code is JourneyWithdrawal.SlotFactsMalformed or JourneyWithdrawal.SlotFactsMismatch);
    }

    /// <summary>车最近报的安全摘要说有门没锁（或有未知）：门没证明关着，拒绝。</summary>
    [Fact]
    public async Task ADoorTheVehicleDoesNotProveClosedRefusesTheWithdrawal()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        await fixture.SetSafetyEvidenceAsync(unknownPresent: false, reasonCodes: ["LOCK_NOT_CLOSED"]);

        await AssertRefusedEverywhereAsync(fixture, await RequestAsync(fixture, blocked), JourneyWithdrawal.DoorNotProvenClosed);
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

        Assert.Equal(("REJECTED", [JourneyWithdrawal.JourneyNotFound]), (refused.Outcome, refused.Codes.ToArray()));
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
        RuntimeFixture fixture, SlotBusinessState reported = SlotBusinessState.Empty)
    {
        await Batch7MultiDemandAdvanceTests.TwoDemandsAtThePickupAsync(fixture);
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
