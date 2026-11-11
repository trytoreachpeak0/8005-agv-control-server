using System.Collections.Immutable;
using System.Text;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Recovery;
using ControlServer.Host.Runtime.Withdrawal;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 人工撤下的服务端在线写法（control-server#771 第二部分）：服务不停、闸不动，撤下经 <see cref="JourneyWithdrawalService"/>
/// 与 Host 接口，判定与写入仍是第一部分那一段（<see cref="JourneyWithdrawal"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 在线多出来的四件事，各有用例：服务端自己读 RIoT（读不到拒、有未结束的单拒、读数进计划与确认码而读数时刻不进）；与运行轮次
/// 经 <see cref="JourneyMutationGate"/> 串行（轮次不结束就拒、一行不写）；收尾快照当场发给在线的车；点名这一趟的恢复授权一并收回并写审计
/// （调度 10-25 定 Q2），点名别的旅程或按车的授权不动。
/// </para>
/// <para>
/// 车在 RIoT 上挂着活单时在线怎么办（Q1）还在等用户定；这里先钉住「拒」，那是三个方案共有的下限。
/// </para>
/// </remarks>
public sealed class JourneyWithdrawalOnlineTests
{
    private const string Operator = "OP-771-2";
    private const string Credential = "journey-withdrawal-credential";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------------------------------------
    // agv03 那一形，在线：不用人填 RIoT 核对，服务端自己读；收尾快照当场发出。
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnOnlineWithdrawalReadsRiotItselfAndSendsTheClosureAtOnce()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await JourneyWithdrawalTests.BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        JourneyWithdrawalRequest request = (await JourneyWithdrawalTests.RequestAsync(fixture, blocked)) with { RiotVerification = null };
        Online online = new(fixture);
        int linesBefore = fixture.Peer.Lines.Count;

        JourneyWithdrawalResult planned = await online.Service().PlanAsync(request, Token);
        Assert.True(planned.Outcome == "PLANNED", string.Join(",", planned.Codes));
        Assert.True(planned.Plan!.RiotOrders is { Known: true, Orders.Count: 0 }, $"{planned.Plan.RiotOrders}");
        Assert.NotNull(planned.RiotObservedAt);

        JourneyWithdrawalResult withdrawn = await online.Service().WithdrawAsync(
            request with { ConfirmationCode = planned.ConfirmationCode }, Token);

        Assert.True(withdrawn.Outcome == "WITHDRAWN", string.Join(",", withdrawn.Codes));
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(
            (JourneyRuntimeStage.Completed, JourneyWithdrawal.WithdrawnReason),
            ((await JourneyOfAsync(fixture, FirstDemandId)).Stage, (await JourneyOfAsync(fixture, FirstDemandId)).BlockReasonCode));
        // Sent now, not left for a reconnect: the closure snapshot reached the vehicle's connection.
        string closure = JourneyClosure.SnapshotMessageIds(blocked.JourneyId)[0];
        Assert.Contains(
            fixture.Peer.Lines.Skip(linesBefore),
            line => Encoding.UTF8.GetString(line).Contains(closure, StringComparison.Ordinal));
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(Token);
        Assert.NotEqual("PENDING_FACT_RECONCILIATION_REQUIRED", session.ReasonCode);
        Assert.NotEqual("OPERATION_RECOVERY_REQUIRED", session.ReasonCode);
    }

    /// <summary>RIoT 读不出完整回答：在线不退回去信人填的文本，拒，一行不写。</summary>
    [Fact]
    public async Task ARiotThatCannotBeReadRefusesTheOnlineWithdrawal()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await JourneyWithdrawalTests.BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        Online online = new(fixture) { Orders = { Known = false } };

        await AssertRefusedOnlineAsync(
            fixture, online, await JourneyWithdrawalTests.RequestAsync(fixture, blocked), JourneyWithdrawal.RiotOrdersUnknown);
    }

    /// <summary>
    /// RIoT 上本车还有一张没结束的单：在线照样拒（D6 第一版；Q1 定了之后方案二在这里改成先取消再撤）。计划里列着那张单。
    /// </summary>
    [Fact]
    public async Task AnUnfinishedOrderInRiotRefusesTheOnlineWithdrawalAndIsNamedInThePlan()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await JourneyWithdrawalTests.BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        Online online = new(fixture);
        online.Orders.Unfinished = ["RIOT-ORDER-771"];
        JourneyWithdrawalRequest request = await JourneyWithdrawalTests.RequestAsync(fixture, blocked);

        JourneyWithdrawalResult planned = await AssertRefusedOnlineAsync(fixture, online, request, JourneyWithdrawal.RiotOrderNotFinished);

        Assert.Equal(
            [new JourneyWithdrawalRiotOrder("RIOT-ORDER-771", null, JourneyWithdrawal.NotThisJourneys)],
            planned.Plan!.RiotOrders!.Orders);
        // Not this journey's: the server does not touch it.
        Assert.Empty(fixture.Riot.OrderCommands);
    }

    /// <summary>
    /// 读数时刻不进确认码：演练与撤下各读一次 RIoT，时刻必然不同；读到的内容没变，码就对得上（否则在线永远撤不下）。
    /// </summary>
    [Fact]
    public async Task TheConfirmationCodeOutlivesALaterRiotReadingThatSaysTheSame()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await JourneyWithdrawalTests.BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        Online online = new(fixture);
        JourneyWithdrawalRequest request = await JourneyWithdrawalTests.RequestAsync(fixture, blocked);
        JourneyWithdrawalResult planned = await online.Service().PlanAsync(request, Token);
        DateTimeOffset firstRead = planned.RiotObservedAt!.Value;
        fixture.Clock.Advance(TimeSpan.FromSeconds(40));

        JourneyWithdrawalResult withdrawn = await online.Service().WithdrawAsync(request with { ConfirmationCode = planned.ConfirmationCode }, Token);

        Assert.True(withdrawn.Outcome == "WITHDRAWN", string.Join(",", withdrawn.Codes));
        Assert.True(withdrawn.RiotObservedAt > firstRead);
    }

    /// <summary>
    /// 一张单在演练之后冒出来（演练时没有）：撤下时重读 RIoT，拒，而且码也过期了——人要重新看一遍。
    /// </summary>
    [Fact]
    public async Task AnOrderThatAppearsAfterTheRehearsalRefusesTheWithdrawal()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await JourneyWithdrawalTests.BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        Online online = new(fixture);
        JourneyWithdrawalRequest request = await JourneyWithdrawalTests.RequestAsync(fixture, blocked);
        JourneyWithdrawalResult planned = await online.Service().PlanAsync(request, Token);
        Assert.Equal("PLANNED", planned.Outcome);
        online.Orders.Unfinished = ["RIOT-ORDER-LATE"];

        JourneyWithdrawalResult withdrawn = await online.Service().WithdrawAsync(request with { ConfirmationCode = planned.ConfirmationCode }, Token);

        Assert.Equal("REJECTED", withdrawn.Outcome);
        Assert.Contains(JourneyWithdrawal.RiotOrderNotFinished, withdrawn.Codes);
        await AssertNothingWithdrawnAsync(fixture);
    }

    // ------------------------------------------------------------------------------------------------
    // 与运行轮次串行。
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 运行轮次拿着闸不放：撤下等到时限就拒（<c>WITHDRAWAL_RUNTIME_BUSY</c>），一行不写；演练只读，不等闸。
    /// </summary>
    [Fact]
    public async Task AWithdrawalWaitsForTheRuntimeRoundAndIsRefusedWhenTheRoundDoesNotEnd()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await JourneyWithdrawalTests.BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        Online online = new(fixture);
        JourneyWithdrawalRequest request = await JourneyWithdrawalTests.RequestAsync(fixture, blocked);
        using IDisposable round = await online.Gate.EnterAsync(Token);

        JourneyWithdrawalResult planned = await online.Service(TimeSpan.FromMilliseconds(50)).PlanAsync(request, Token);
        Assert.Equal("PLANNED", planned.Outcome);
        JourneyWithdrawalResult withdrawn = await online.Service(TimeSpan.FromMilliseconds(50))
            .WithdrawAsync(request with { ConfirmationCode = planned.ConfirmationCode }, Token);

        Assert.Equal("REJECTED", withdrawn.Outcome);
        Assert.Equal([JourneyWithdrawal.RuntimeBusy], withdrawn.Codes);
        await AssertNothingWithdrawnAsync(fixture);
    }

    /// <summary>
    /// 派车轮在撤下之前读到这趟旅程可以追加，撤下先提交了，追加再提交：不写错数据——新需求不挂到已收尾的旅程上，旅程仍是撤下的样子。
    /// 追加那一侧得到 <see cref="JourneyEndedBeforeAppendException"/>：cs#864 合入后派车轮把它当这一轮的结局、不记 Error。
    /// </summary>
    [Fact]
    public async Task AnAppendCommittedAfterTheWithdrawalWritesNothing()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow onItsWay = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, onItsWay.Stage);
        // The round read the journey as open to an append before the withdrawal ran.
        await using ControlServerDbContext round = new(fixture.DbOptionsForTests);
        Assert.NotEqual(
            JourneyRuntimeStage.Completed,
            (await round.JourneyRuntimes.SingleAsync(row => row.JourneyId == onItsWay.JourneyId, Token)).Stage);
        Online online = new(fixture);
        JourneyWithdrawalRequest request = JourneyWithdrawalTests.Request(onItsWay, slotFacts: null, cargoLocation: null);
        JourneyWithdrawalResult planned = await online.Service().PlanAsync(request, Token);
        Assert.True(planned.Outcome == "PLANNED", string.Join(",", planned.Codes));
        Assert.Equal(
            "WITHDRAWN",
            (await online.Service().WithdrawAsync(request with { ConfirmationCode = planned.ConfirmationCode }, Token)).Outcome);
        fixture.Context.ChangeTracker.Clear();
        int membershipsBefore = await fixture.Context.Set<JourneyDemandRow>().CountAsync(row => row.JourneyId == onItsWay.JourneyId, Token);
        int stopsBefore = await fixture.Context.Set<JourneyStopRow>().CountAsync(row => row.JourneyId == onItsWay.JourneyId, Token);
        const string appended = "D-APPEND-771";

        Exception? error = await Record.ExceptionAsync(() => new WireToGateStore(round).AppendToJourneyAsync(
            Batch7JourneyFixture.Snapshot(appended, Now),
            new JourneyAppendPlan(
                onItsWay.JourneyId,
                appended,
                Batch7JourneyFixture.Plan(appended, fixture.Options.AgvId, fixture.Options.VehicleKey, Now),
                JourneyIdentity.AppendedPickupStopId(appended),
                JourneyIdentity.UnloadStopId(onItsWay.JourneyId),
                fixture.Options.DispatchZone,
                DispatchZoneParameterVersion: 1,
                [
                    new JourneyStopSequenceChange(JourneyIdentity.PickupStopId(onItsWay.JourneyId), 1),
                    new JourneyStopSequenceChange(JourneyIdentity.AppendedPickupStopId(appended), 2),
                    new JourneyStopSequenceChange(JourneyIdentity.UnloadStopId(onItsWay.JourneyId), 3),
                ],
                Now),
            Token));

        // Since control-server#864 the store says exactly this, and the dispatch round turns it into the demand's outcome for the
        // round (DemandIntakeOutcome.JourneyEnded) instead of an Error 2124 -- MultiVehicleExecutionTests.AppendToEndedJourney pins
        // the log side. So a withdrawal landing between the round's read and its commit is the race #864 settles, nothing new.
        Assert.IsType<JourneyEndedBeforeAppendException>(error);
        fixture.Context.ChangeTracker.Clear();
        JourneyRuntimeRow after = await fixture.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == onItsWay.JourneyId, Token);
        Assert.Equal((JourneyRuntimeStage.Completed, JourneyWithdrawal.WithdrawnReason), (after.Stage, after.BlockReasonCode));
        Assert.Equal(membershipsBefore, await fixture.Context.Set<JourneyDemandRow>().CountAsync(row => row.JourneyId == onItsWay.JourneyId, Token));
        Assert.Equal(stopsBefore, await fixture.Context.Set<JourneyStopRow>().CountAsync(row => row.JourneyId == onItsWay.JourneyId, Token));
        Assert.False(await fixture.Context.AcceptedDemands.AnyAsync(row => row.DemandId == appended, Token));
    }

    // ------------------------------------------------------------------------------------------------
    // 恢复授权（调度 10-25 定 Q2：点名这一趟的一并收回，写审计）。
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheGrantNamingTheWithdrawnJourneyIsRevokedWithItAndAudited()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await JourneyWithdrawalTests.BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        Online online = new(fixture);
        RecoveryGrant grant = online.GrantFor(blocked.JourneyId);

        JourneyWithdrawalResult withdrawn = await online.PlanAndWithdrawAsync(await JourneyWithdrawalTests.RequestAsync(fixture, blocked));

        Assert.Equal(grant.GrantId, withdrawn.RevokedGrantId);
        Assert.Null(online.Grants.Current(fixture.Options.AgvId));
        AdministratorAuditRecordRow revocation = await fixture.Context.Set<AdministratorAuditRecordRow>().AsNoTracking()
            .SingleAsync(row => row.Action == RecoveryGrantIssuance.RevokeAuditAction, Token);
        Assert.Equal(GovernanceActionOutcome.Succeeded, revocation.Outcome);
        foreach (string said in new[] { grant.GrantId, blocked.JourneyId, Operator, withdrawn.AuditRecordId! })
        {
            Assert.Contains(said, revocation.DetailJson, StringComparison.Ordinal);
        }
    }

    /// <summary>授权点名的是别的旅程，或者是按车的（不点名旅程）：撤下不碰它，也不写收回审计。</summary>
    [Theory]
    [InlineData("journey:another-771")]
    [InlineData(null)]
    public async Task AGrantNamingAnotherJourneyOrNoneIsLeftAlone(string? journeyId)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await JourneyWithdrawalTests.BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        Online online = new(fixture);
        RecoveryGrant grant = online.GrantFor(journeyId);

        JourneyWithdrawalResult withdrawn = await online.PlanAndWithdrawAsync(await JourneyWithdrawalTests.RequestAsync(fixture, blocked));

        Assert.Null(withdrawn.RevokedGrantId);
        Assert.Equal(grant.GrantId, online.Grants.Current(fixture.Options.AgvId)?.GrantId);
        Assert.False(await fixture.Context.Set<AdministratorAuditRecordRow>().AnyAsync(
            row => row.Action == RecoveryGrantIssuance.RevokeAuditAction, Token));
    }

    /// <summary>撤下被拒：授权不动。</summary>
    [Fact]
    public async Task ARefusedWithdrawalLeavesTheGrantInEffect()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await JourneyWithdrawalTests.BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        Online online = new(fixture);
        online.Orders.Unfinished = ["RIOT-ORDER-771"];
        RecoveryGrant grant = online.GrantFor(blocked.JourneyId);

        JourneyWithdrawalResult withdrawn = await online.Service().WithdrawAsync(
            (await JourneyWithdrawalTests.RequestAsync(fixture, blocked)) with { ConfirmationCode = "ANY" }, Token);

        Assert.Equal("REJECTED", withdrawn.Outcome);
        Assert.Equal(grant.GrantId, online.Grants.Current(fixture.Options.AgvId)?.GrantId);
    }

    // ------------------------------------------------------------------------------------------------
    // Host 接口。
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheEntryIsUnavailableWithoutItsCredentialVariableAndRefusesAWrongOne()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await JourneyWithdrawalTests.BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        Online online = new(fixture);
        JourneyWithdrawalHttpRequest request = HttpRequest(await JourneyWithdrawalTests.RequestAsync(fixture, blocked));

        var unavailable = await online.PostAsync("CONTROL_SERVER_TEST_MISSING_" + Guid.NewGuid().ToString("N"), "Bearer x", request);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ProblemHttpResult>(unavailable.Result).StatusCode);
        using CredentialScope scope = new();
        var wrong = await online.PostAsync(scope.Variable, "Bearer wrong", request);
        Assert.IsType<UnauthorizedHttpResult>(wrong.Result);

        await AssertNothingWithdrawnAsync(fixture);
        Assert.False(await fixture.Context.Set<AdministratorAuditRecordRow>().AnyAsync(Token));
    }

    /// <summary>不带确认码是演练（200 PLANNED、给码），带码是撤下（200 WITHDRAWN、审计号）。</summary>
    [Fact]
    public async Task TheEntryRehearsesWithoutACodeAndWithdrawsWithIt()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await JourneyWithdrawalTests.BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        Online online = new(fixture);
        using CredentialScope scope = new();
        JourneyWithdrawalHttpRequest request = HttpRequest(await JourneyWithdrawalTests.RequestAsync(fixture, blocked));

        JourneyWithdrawalResponse planned = Assert.IsType<Ok<JourneyWithdrawalResponse>>(
            (await online.PostAsync(scope.Variable, $"Bearer {Credential}", request)).Result).Value!;
        Assert.Equal("PLANNED", planned.Outcome);
        Assert.Null(planned.AuditRecordId);
        JourneyWithdrawalResponse withdrawn = Assert.IsType<Ok<JourneyWithdrawalResponse>>(
            (await online.PostAsync(scope.Variable, $"Bearer {Credential}", request with { ConfirmationCode = planned.ConfirmationCode })).Result).Value!;

        Assert.Equal("WITHDRAWN", withdrawn.Outcome);
        Assert.NotNull(withdrawn.AuditRecordId);
        Assert.Equal(JourneyRuntimeStage.Completed, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
    }

    /// <summary>拒绝是 409，列全部理由码；过期的码附新计划与新码。</summary>
    [Fact]
    public async Task ARefusalIsAConflictListingEveryCode()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await JourneyWithdrawalTests.BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        Online online = new(fixture);
        using CredentialScope scope = new();
        JourneyWithdrawalHttpRequest request = HttpRequest(await JourneyWithdrawalTests.RequestAsync(fixture, blocked)) with
        {
            OperatorId = null,
            ConfirmationCode = "STALE-CODE"
        };

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>((await online.PostAsync(scope.Variable, $"Bearer {Credential}", request)).Result);

        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        Assert.Contains(
            JourneyWithdrawal.OperatorRequired,
            Assert.IsAssignableFrom<IReadOnlyList<string>>(problem.ProblemDetails.Extensions["codes"]));
        Assert.NotNull(problem.ProblemDetails.Extensions["auditRecordId"]);
        await AssertNothingWithdrawnAsync(fixture);
    }

    /// <summary>轮次不结束：503，一行不写。</summary>
    [Fact]
    public async Task ARuntimeRoundThatDoesNotEndIsServiceUnavailable()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow blocked = await JourneyWithdrawalTests.BlockOnAnUnknownLoadWithAnotherDemandStillToLoadAsync(fixture);
        Online online = new(fixture) { GateTimeout = TimeSpan.FromMilliseconds(50) };
        using CredentialScope scope = new();
        using IDisposable round = await online.Gate.EnterAsync(Token);

        ProblemHttpResult problem = Assert.IsType<ProblemHttpResult>((await online.PostAsync(
            scope.Variable,
            $"Bearer {Credential}",
            HttpRequest(await JourneyWithdrawalTests.RequestAsync(fixture, blocked)) with { ConfirmationCode = "ANY" })).Result);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        await AssertNothingWithdrawnAsync(fixture);
    }

    // ------------------------------------------------------------------------------------------------
    // 方案二（用户 10-25 选）：车在 RIoT 上还挂着这一趟自己的单，服务端先取消、确认之后再撤。10-18 agv01 那一形：车开往取货站。
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 演练列出那张单、要先取消，并列出车此刻的位置与速度（调度 10-25 要求）；撤下先经订单命令面取消（先武装审计行再发），对账确认后撤，
    /// 需求退回待派。撤完之后引擎再跑一轮，不为这张单重建、不建任何新单。
    /// </summary>
    [Fact]
    public async Task AnOrderOfThisJourneyStillRunningIsCancelledFirstAndThenTheJourneyIsWithdrawn()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow onItsWay = await OnItsWayToThePickupAsync(fixture);
        OrderIntentRow pickup = await PickupIntentAsync(fixture, onItsWay);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = 12, Speed = 0.8 };
        Online online = new(fixture);
        JourneyWithdrawalRequest request = JourneyWithdrawalTests.Request(onItsWay, slotFacts: null, cargoLocation: null);

        JourneyWithdrawalResult planned = await online.Service().PlanAsync(request, Token);

        Assert.True(planned.Outcome == "PLANNED", string.Join(",", planned.Codes));
        Assert.Equal(
            [new JourneyWithdrawalRiotOrder(pickup.OrderId!, pickup.UpperId, JourneyWithdrawal.CancelBeforeWithdrawal)],
            planned.Plan!.RiotOrders!.Orders);
        Assert.True(planned.Vehicle is { CurrentStationId: 12, Speed: 0.8 }, $"{planned.Vehicle}");
        Assert.Empty(fixture.Riot.OrderCommands);

        int createsBefore = fixture.Context.OrderIntents.Count();
        JourneyWithdrawalResult withdrawn = await online.Service().WithdrawAsync(request with { ConfirmationCode = planned.ConfirmationCode }, Token);

        Assert.True(withdrawn.Outcome == "WITHDRAWN", string.Join(",", withdrawn.Codes));
        Assert.Equal(
            [new JourneyWithdrawalOrderCancellation(pickup.OrderId!, pickup.UpperId, nameof(RiotOrderCommandOutcome.Confirmed))],
            withdrawn.Cancellations);
        Assert.Equal([(RiotOrderCommandKind.Cancel, pickup.OrderId!)], fixture.Riot.OrderCommands.Select(item => (item.Kind, item.OrderId)));
        fixture.Context.ChangeTracker.Clear();
        Assert.True(await fixture.Context.RiotOrderCommandAudit.AnyAsync(
            row => row.TargetUpperId == pickup.UpperId && row.CommandType == RiotCommandTypeNames.CancelOrder, Token));
        Assert.Equal(JourneyRuntimeStage.Completed, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        Assert.Contains(
            FirstDemandId,
            await DemandJourneyLookup.ReleasedForRedispatch(fixture.Context).AsNoTracking().Select(row => row.DemandId).ToArrayAsync(Token));

        Assert.Null(await UnloadCrashResumeTests.RunRoundAsync(fixture));
        fixture.Context.ChangeTracker.Clear();
        Assert.Empty(await fixture.Context.OwnOrderRebuilds.AsNoTracking()
            .Where(row => row.JourneyId == onItsWay.JourneyId && row.State != OwnOrderRebuildStates.Ended).ToArrayAsync(Token));
        Assert.Equal(createsBefore, await fixture.Context.OrderIntents.CountAsync(Token));
    }

    /// <summary>
    /// RIoT 接了取消却没把单结束：对账不是 Confirmed，就不撤——一行不改，审计记一条失败、列出取消结果，人去 RIoT 看。
    /// </summary>
    [Fact]
    public async Task ACancellationRiotDoesNotConfirmLeavesTheJourneyAsItWas()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow onItsWay = await OnItsWayToThePickupAsync(fixture);
        fixture.Riot.CancelTakesEffect = false;
        Online online = new(fixture);
        JourneyWithdrawalRequest request = JourneyWithdrawalTests.Request(onItsWay, slotFacts: null, cargoLocation: null);
        JourneyWithdrawalResult planned = await online.Service().PlanAsync(request, Token);

        JourneyWithdrawalResult withdrawn = await online.Service().WithdrawAsync(request with { ConfirmationCode = planned.ConfirmationCode }, Token);

        Assert.Equal("REJECTED", withdrawn.Outcome);
        Assert.Equal([JourneyWithdrawal.OrderCancelNotConfirmed], withdrawn.Codes);
        Assert.NotEqual(nameof(RiotOrderCommandOutcome.Confirmed), Assert.Single(withdrawn.Cancellations!).Outcome);
        await AssertNothingWithdrawnAsync(fixture);
        AdministratorAuditRecordRow audit = await fixture.Context.Set<AdministratorAuditRecordRow>().AsNoTracking()
            .SingleAsync(row => row.Action == JourneyWithdrawal.AuditAction, Token);
        Assert.Equal(GovernanceActionOutcome.Failed, audit.Outcome);
        Assert.Contains(JourneyWithdrawal.OrderCancelNotConfirmed, audit.DetailJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// 上一次取消没确认，过一会儿 RIoT 好了：重新演练、再按，服务端再发一次取消（新的一次尝试，不覆盖旧的），确认后撤下。
    /// </summary>
    [Fact]
    public async Task AWithdrawalRetriedAfterAnUnconfirmedCancellationCancelsAgainAndWithdraws()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow onItsWay = await OnItsWayToThePickupAsync(fixture);
        fixture.Riot.CancelTakesEffect = false;
        Online online = new(fixture);
        JourneyWithdrawalRequest request = JourneyWithdrawalTests.Request(onItsWay, slotFacts: null, cargoLocation: null);
        JourneyWithdrawalResult first = await online.Service().PlanAsync(request, Token);
        Assert.Equal("REJECTED", (await online.Service().WithdrawAsync(request with { ConfirmationCode = first.ConfirmationCode }, Token)).Outcome);
        fixture.Riot.CancelTakesEffect = true;

        JourneyWithdrawalResult again = await online.Service().PlanAsync(request, Token);
        Assert.True(again.Outcome == "PLANNED", string.Join(",", again.Codes));
        JourneyWithdrawalResult withdrawn = await online.Service().WithdrawAsync(request with { ConfirmationCode = again.ConfirmationCode }, Token);

        Assert.True(withdrawn.Outcome == "WITHDRAWN", string.Join(",", withdrawn.Codes));
        Assert.Equal(2, fixture.Riot.OrderCommands.Count(item => item.Kind == RiotOrderCommandKind.Cancel));
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(2, await fixture.Context.RiotOrderCommandAudit.CountAsync(row => row.CommandType == RiotCommandTypeNames.CancelOrder, Token));
    }

    /// <summary>
    /// 取消与撤下之间引擎跑了一轮，看见本服务端自己取消的单：不重建，把旅程标成等人（<c>ORDER_ENDED_WITHOUT_ARRIVAL</c>）。这一笔是取消本身
    /// 带来的，不算「演练之后状态变了」：撤下照样成。
    /// </summary>
    [Fact]
    public async Task AnEngineRoundBetweenTheCancellationAndTheWithdrawalDoesNotStaleTheConfirmation()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow onItsWay = await OnItsWayToThePickupAsync(fixture);
        Online online = new(fixture);
        JourneyWithdrawalRequest request = JourneyWithdrawalTests.Request(onItsWay, slotFacts: null, cargoLocation: null);
        JourneyWithdrawalResult planned = await online.Service().PlanAsync(request, Token);
        string? seenBetween = null;
        // Read 1 was the rehearsal, read 2 the withdrawal's first; read 3 is the one after the cancellation.
        online.Orders.BeforeRead = async read =>
        {
            if (read != 3) return;
            fixture.Context.ChangeTracker.Clear();
            await fixture.Engine.ExecuteOnceAsync(Token);
            fixture.Context.ChangeTracker.Clear();
            seenBetween = (await JourneyOfAsync(fixture, FirstDemandId)).BlockReasonCode;
        };

        JourneyWithdrawalResult withdrawn = await online.Service().WithdrawAsync(request with { ConfirmationCode = planned.ConfirmationCode }, Token);

        Assert.Equal(JourneyRuntimeEngine.OrderEndedWithoutArrivalReason, seenBetween);
        Assert.True(withdrawn.Outcome == "WITHDRAWN", string.Join(",", withdrawn.Codes));
        Assert.Equal(JourneyRuntimeStage.Completed, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
    }

    /// <summary>
    /// 取消之后、撤下之前，有一件与取消无关的事变了（这里是车上开了一个恢复会话）：撤下重判每一条准入，拒；单已经取消了，结果里照列。
    /// </summary>
    [Fact]
    public async Task EveryCriterionIsJudgedAgainAfterTheCancellation()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow onItsWay = await OnItsWayToThePickupAsync(fixture);
        Online online = new(fixture);
        JourneyWithdrawalRequest request = JourneyWithdrawalTests.Request(onItsWay, slotFacts: null, cargoLocation: null);
        JourneyWithdrawalResult planned = await online.Service().PlanAsync(request, Token);
        online.Orders.BeforeRead = async read =>
        {
            if (read != 3) return;
            fixture.Context.ExceptionRecoverySessions.Add(new ExceptionRecoverySessionRow
            {
                ExceptionRecoverySessionId = "ers-771-2",
                RequestId = "req-771-2",
                RequestContentHash = new string('1', 64),
                AgvId = fixture.Options.AgvId,
                EventId = "evt-771-2",
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
        };

        JourneyWithdrawalResult withdrawn = await online.Service().WithdrawAsync(request with { ConfirmationCode = planned.ConfirmationCode }, Token);

        Assert.Equal("REJECTED", withdrawn.Outcome);
        Assert.Contains(JourneyWithdrawal.RecoverySessionOpen, withdrawn.Codes);
        Assert.Equal(nameof(RiotOrderCommandOutcome.Confirmed), Assert.Single(withdrawn.Cancellations!).Outcome);
        await AssertNothingWithdrawnAsync(fixture);
    }

    /// <summary>
    /// 取消发出去了、单也从这辆车未结束的单里消失了，但对账读回说不清（<c>Unknown</c>）：「核到终态再撤」，这一次不撤。按车列出的单与按单号
    /// 对账是两次读，前者没了不等于后者确认了取消；这一道只在服务端（撤下核心第二步看的是前者）。
    /// </summary>
    [Fact]
    public async Task ACancellationWhoseReadBackIsUnknownWithdrawsNothingEvenWhenTheOrderIsNoLongerListed()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow onItsWay = await OnItsWayToThePickupAsync(fixture);
        Online online = new(fixture) { CommandReadBack = new UnknownReadBack(fixture.Riot) };
        JourneyWithdrawalRequest request = JourneyWithdrawalTests.Request(onItsWay, slotFacts: null, cargoLocation: null);
        JourneyWithdrawalResult planned = await online.Service().PlanAsync(request, Token);

        JourneyWithdrawalResult withdrawn = await online.Service().WithdrawAsync(request with { ConfirmationCode = planned.ConfirmationCode }, Token);

        Assert.Equal(("REJECTED", JourneyWithdrawal.OrderCancelNotConfirmed), (withdrawn.Outcome, Assert.Single(withdrawn.Codes)));
        Assert.Equal(nameof(RiotOrderCommandOutcome.Unknown), Assert.Single(withdrawn.Cancellations!).Outcome);
        // The cancel did take effect in RIoT: the order is gone from the vehicle's list.
        Assert.DoesNotContain(
            (await fixture.Riot.ListUnfinishedOrdersAsync(Token)).Orders,
            order => order.ExecuteVehicleKey == fixture.Options.VehicleKey || order.AppointVehicleKey == fixture.Options.VehicleKey);
        await AssertNothingWithdrawnAsync(fixture);
    }

    /// <summary>确认码对不上（演练之后状态变了）：在发任何取消之前就拒，一张单都不取消。</summary>
    [Fact]
    public async Task AStaleConfirmationCancelsNothing()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow onItsWay = await OnItsWayToThePickupAsync(fixture);
        Online online = new(fixture);
        JourneyWithdrawalRequest request = JourneyWithdrawalTests.Request(onItsWay, slotFacts: null, cargoLocation: null);

        JourneyWithdrawalResult withdrawn = await online.Service().WithdrawAsync(request with { ConfirmationCode = "000000000000" }, Token);

        Assert.Equal(("REJECTED", JourneyWithdrawal.ConfirmationStale), (withdrawn.Outcome, Assert.Single(withdrawn.Codes)));
        Assert.Empty(fixture.Riot.OrderCommands);
        await AssertNothingWithdrawnAsync(fixture);
    }

    /// <summary>
    /// control-server#866 之后，开往取货站途中会话不在、车又停在交通检查点时，旅程码是 <c>VEHICLE_WAITING_AT_CHECKPOINT</c> 而不是
    /// <c>ONBOARD_SESSION_NOT_READY</c>。撤下对取货段不看码：照样能演练、照样先取消这一趟自己的单（10-23 agv01 在检查点等了两个多小时就是这一形）。
    /// </summary>
    [Fact]
    public async Task AJourneyHeldAtACheckpointOnItsWayToThePickupIsStillWithdrawable()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow onItsWay = await OnItsWayToThePickupAsync(fixture);
        OrderIntentRow pickup = await PickupIntentAsync(fixture, onItsWay);
        JourneyRuntimeRow row = await fixture.Context.JourneyRuntimes.SingleAsync(item => item.JourneyId == onItsWay.JourneyId, Token);
        row.SetBlockReason(JourneyRuntimeEngine.CheckpointWaitReason, Now);
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        Online online = new(fixture);

        JourneyWithdrawalResult planned = await online.Service()
            .PlanAsync(JourneyWithdrawalTests.Request(onItsWay, slotFacts: null, cargoLocation: null), Token);

        Assert.True(planned.Outcome == "PLANNED", string.Join(",", planned.Codes));
        Assert.Equal(JourneyRuntimeEngine.CheckpointWaitReason, planned.Plan!.BlockReasonCode);
        Assert.Equal(
            [new JourneyWithdrawalRiotOrder(pickup.OrderId!, pickup.UpperId, JourneyWithdrawal.CancelBeforeWithdrawal)],
            planned.Plan.RiotOrders!.Orders);
    }

    private static async Task<JourneyRuntimeRow> OnItsWayToThePickupAsync(RuntimeFixture fixture)
    {
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow onItsWay = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, onItsWay.Stage);
        fixture.Context.ChangeTracker.Clear();
        return onItsWay;
    }

    private static async Task<OrderIntentRow> PickupIntentAsync(RuntimeFixture fixture, JourneyRuntimeRow journey)
    {
        OrderIntentRow intent = await fixture.Context.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.UpperId == journey.PickupUpperId, Token);
        Assert.NotNull(intent.OrderId);
        return intent;
    }

    // ------------------------------------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------------------------------------

    private static async Task<JourneyWithdrawalResult> AssertRefusedOnlineAsync(
        RuntimeFixture fixture, Online online, JourneyWithdrawalRequest request, string code)
    {
        JourneyWithdrawalResult planned = await online.Service().PlanAsync(request, Token);
        Assert.True(
            planned.Outcome == "REJECTED" && planned.Codes.Contains(code),
            $"plan: {planned.Outcome} [{string.Join(",", planned.Codes)}], expected {code}");
        JourneyWithdrawalResult withdrawn = await online.Service().WithdrawAsync(request with { ConfirmationCode = "ANY-CODE" }, Token);
        Assert.True(
            withdrawn.Outcome == "REJECTED" && withdrawn.Codes.Contains(code),
            $"withdraw: {withdrawn.Outcome} [{string.Join(",", withdrawn.Codes)}], expected {code}");
        await AssertNothingWithdrawnAsync(fixture);
        return planned;
    }

    private static async Task AssertNothingWithdrawnAsync(RuntimeFixture fixture)
    {
        fixture.Context.ChangeTracker.Clear();
        Assert.Empty(await fixture.Context.JourneyRuntimes.AsNoTracking()
            .Where(row => row.BlockReasonCode == JourneyWithdrawal.WithdrawnReason).ToArrayAsync(Token));
        Assert.Empty(await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .Where(row => row.RemovedAt != null).ToArrayAsync(Token));
    }

    private static JourneyWithdrawalHttpRequest HttpRequest(JourneyWithdrawalRequest request) =>
        new(
            request.JourneyId, request.VehicleKey, request.OperatorId, request.Reason, request.SiteVerification,
            request.RiotVerification, request.SlotFacts, request.CargoLocation, request.ClaimedRole, request.ConfirmationCode);

    /// <summary>The running server around one fixture: its RIoT order reading, its round gate, its grants.</summary>
    private sealed class Online(RuntimeFixture fixture)
    {
        public RiotOrders Orders { get; } = new(fixture);

        public JourneyMutationGate Gate { get; } = new();

        public RecoveryGrantRegistry Grants { get; } = new(fixture.Clock);

        public TimeSpan GateTimeout { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>When set, how the order command surface reads an order back after a command (default: the fixture's RIoT).</summary>
        public IRiotMovementGateway? CommandReadBack { get; set; }

        public JourneyWithdrawalService Service(TimeSpan? gateTimeout = null) => new(
            fixture.Context,
            new GovernanceStore(fixture.Context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default),
            Orders,
            fixture.Riot,
            new RiotOrderCommandService(
                fixture.Riot, new RiotOrderCommandAuditStore(fixture.Context), CommandReadBack ?? fixture.Riot, fixture.Clock),
            Gate,
            new OnboardJourneyPublisher(new WireToGateStore(fixture.Context), fixture.Peer, fixture.Clock),
            Grants,
            fixture.Clock,
            gateTimeout ?? GateTimeout);

        public RecoveryGrant GrantFor(string? journeyId)
        {
            RecoveryGrant grant = new(
                "grant-771",
                fixture.Options.AgvId,
                fixture.Options.VehicleKey,
                journeyId,
                RecoveryGrantActions.Default,
                "maintenance-771",
                fixture.Clock.GetUtcNow(),
                fixture.Clock.GetUtcNow().AddMinutes(30),
                "audit-grant-771");
            Grants.Grant(grant);
            return grant;
        }

        public async Task<JourneyWithdrawalResult> PlanAndWithdrawAsync(JourneyWithdrawalRequest request)
        {
            request = request with { OperatorId = Operator };
            JourneyWithdrawalResult planned = await Service().PlanAsync(request, Token);
            Assert.True(planned.Outcome == "PLANNED", string.Join(",", planned.Codes));
            JourneyWithdrawalResult withdrawn = await Service().WithdrawAsync(request with { ConfirmationCode = planned.ConfirmationCode }, Token);
            Assert.True(withdrawn.Outcome == "WITHDRAWN", string.Join(",", withdrawn.Codes));
            fixture.Context.ChangeTracker.Clear();
            return withdrawn;
        }

        public Task<Results<Ok<JourneyWithdrawalResponse>, UnauthorizedHttpResult, ProblemHttpResult>> PostAsync(
            string credentialVariable, string authorization, JourneyWithdrawalHttpRequest request)
        {
            DefaultHttpContext http = new() { RequestServices = new ServiceCollection().BuildServiceProvider() };
            http.Request.Scheme = "http";
            http.Request.Headers.Authorization = authorization;
            return JourneyWithdrawalEndpoints.HandleAsync(
                http,
                request,
                Service(),
                Options.Create(new VehicleFaultRecoveryOptions { Enabled = true, CredentialEnvironmentVariable = credentialVariable }),
                Token);
        }
    }

    /// <summary>
    /// RIoT's list of this vehicle's unfinished orders: the fixture's own live orders on the vehicle (this server's, as the engine
    /// created them, cancelled when a cancel takes effect) and whatever the test adds; read at the fixture clock.
    /// </summary>
    private sealed class RiotOrders(RuntimeFixture fixture) : IRiotVehicleOrderFacts
    {
        public bool Known { get; set; } = true;

        public string[] Unfinished { get; set; } = [];

        /// <summary>How many reads so far.</summary>
        public int Reads { get; private set; }

        /// <summary>Runs before a read, with that read's number (1-based): to land something between two reads.</summary>
        public Func<int, Task>? BeforeRead { get; set; }

        public async Task<RiotVehicleOrderObservation> ReadUnfinishedOrdersAsync(string deviceKey, CancellationToken cancellationToken)
        {
            Reads++;
            if (BeforeRead is { } before)
            {
                await before(Reads);
            }
            if (!Known)
            {
                return new RiotVehicleOrderObservation(deviceKey, null, [], fixture.Clock.GetUtcNow());
            }
            RiotUnfinishedOrderListing listing = await fixture.Riot.ListUnfinishedOrdersAsync(cancellationToken);
            string[] ids =
            [
                .. listing.Orders
                    .Where(order => order.ExecuteVehicleKey == deviceKey || order.AppointVehicleKey == deviceKey)
                    .Select(order => order.OrderId),
                .. Unfinished
            ];
            return new RiotVehicleOrderObservation(deviceKey, ids.Length > 0, ids, fixture.Clock.GetUtcNow());
        }
    }

    /// <summary>An order read-back that cannot say: what a failed RIoT read after the command looks like.</summary>
    private sealed class UnknownReadBack(IRiotMovementGateway inner) : IRiotMovementGateway
    {
        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken) =>
            Task.FromResult(new RiotOrderObservation(upperId, RiotOrderObservationKind.Unknown, null));

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken) =>
            inner.CreateAsync(intent, cancellationToken);
    }

    private sealed class CredentialScope : IDisposable
    {
        public CredentialScope()
        {
            Environment.SetEnvironmentVariable(Variable, Credential);
        }

        public string Variable { get; } = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");

        public void Dispose() => Environment.SetEnvironmentVariable(Variable, null);
    }
}
