using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.StagingPickupNoCountdownTests;

namespace ControlServer.Tests;

/// <summary>
/// 车载屏上的「出发」（control-server#713，CP-0012，协议 4.0.0 候选的 <c>CurrentStopDepartureRequested</c> / <c>CurrentStopDepartureResult</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 夹具是 <see cref="StagingPickupNoCountdownTests"/> 那一份：STAGING_TO_WIRE 在派工待送站 305 取货、WIRE_TO_GATE 在机台 N1-1 取货，同一组事实跑
/// 「待送点（规则开）」「机台（规则开）」「待送点（规则关）」三种。请求像扫码录入一样落进收件箱（入站只回 <c>DurableAck</c>），由引擎下一轮答。
/// </para>
/// <para>
/// 每条用例对应票面验收的编号；带 <c>ProtocolVector</c> trait 的那几条同时是协议 v4 候选向量的同名测试。
/// </para>
/// </remarks>
public sealed class StagingDepartureRequestTests
{
    private const string ThirdDemand = "10000000-0000-4000-8000-000000000713";

    private const string ThirdSublot = "SUBLOT-713";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 验收 1：待送点装了一篮、同站还挂着两条没装（追加进来的两个停靠），客户点出发。两条回到积压（受理标记清掉、需求仍 Accepted、无抑制、
    /// 不计站点超时次数），装货阶段以 <c>OPERATOR_DEPARTED</c> 关闭，车走正常离站（发离站核验）去送那一篮。答复 <c>ACCEPTED</c> 先于业务快照。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-ACCEPTED")]
    public async Task DepartureAcceptedReleasesWhatIsNotLoadedAndLeavesWithWhatIs()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await LoadTheBasketAsync(fixture);
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        await AppendStagingDemandAsync(fixture, ThirdDemand, ThirdSublot);
        await fixture.ProveSlotDoorsClosedAsync();
        int wireBefore = fixture.Peer.Lines.Count;

        string departureRequestId = await RequestDepartureAsync(fixture);
        await RunAsync(fixture);

        JsonElement result = await ResultAsync(fixture, departureRequestId);
        Assert.Equal("ACCEPTED", result.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("problem").ValueKind);

        foreach (string demandId in new[] { SecondDemand, ThirdDemand })
        {
            JourneyDemandRow released = await MembershipAsync(fixture, demandId);
            Assert.NotNull(released.RemovedAt);
            Assert.Equal(DemandJourneyLookup.ReleasedForRedispatchReason, released.RemovalReason);
            Assert.Equal(DemandExecutionStatus.Accepted, (await DemandAsync(fixture, demandId)).Status);
            // 夹具直接追加的需求没有积压行（派车轮追加的才有）；有就必须清掉受理标记。
            Assert.Null((await fixture.Context.JourneyBacklog.AsNoTracking()
                .SingleOrDefaultAsync(row => row.DemandId == demandId, Token))?.AcceptedAt);
            Assert.True(
                await DemandJourneyLookup.ReleasedForRedispatch(fixture.Context).AnyAsync(row => row.DemandId == demandId, Token),
                $"{demandId} is not waiting to be dispatched again.");
        }
        Assert.Equal(0, await StationDeadlineReleasesAsync(fixture));
        await SuppressionAssertions.AssertNothingSuppressedAsync(fixture.Context);

        JourneyDemandRow loaded = await MembershipAsync(fixture, Demand);
        Assert.Equal((JourneyDemandStatuses.Loaded, (DateTimeOffset?)null), (loaded.Status, loaded.RemovedAt));
        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal((LoadingPhaseStates.Closed, LoadingClosedReasons.OperatorDeparted), (journey.LoadingPhaseState, journey.LoadingClosedReason));
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, journey.Stage);
        Assert.True(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "The vehicle did not leave through the safety check.");

        // 线上的先后：答复在前，装货阶段关闭的那张业务快照在后，离站核验最后（向量的 orderedExpectedMessages）。
        // 这一轮开头的补发会先把车还没确认的旧行再发一遍，所以从答复那一行往后找。本站（第 1 个停靠）上没有放回的需求，不发空清单。
        string[] sent = [.. fixture.Peer.Lines.Skip(wireBefore).Select(MessageTypeOf)];
        int resultAt = Array.IndexOf(sent, CurrentStopDepartures.ResultType);
        Assert.True(resultAt >= 0, string.Join(",", sent));
        Assert.Equal(
            ["VehicleBusinessStateSnapshot", "UpcomingStopPlanSnapshot", "PreDepartureSafetyCheck"],
            sent.Skip(resultAt + 1));
        Batch7CargoHoldingTests.LoadingPhaseSnapshot last = (await Batch7CargoHoldingTests.LoadingPhaseSnapshotsAsync(fixture))
            .OrderBy(snapshot => snapshot.Revision).Last();
        Assert.Equal(LoadingClosedReasons.OperatorDeparted, last.ClosedReason);
        Assert.Contains(fixture.EngineLog.Entries, entry =>
            entry.Message.Contains(departureRequestId, StringComparison.Ordinal) &&
            entry.Message.Contains("OP-713", StringComparison.Ordinal) &&
            entry.Message.Contains("2 demand(s) released", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验收 2：持货等单（本站装完、清单上没有待装）时点出发：什么也不放回、不补一张空清单，装货阶段以 <c>OPERATOR_DEPARTED</c> 关闭，直接走正常离站。
    /// 空清单的 <c>operationSessionId</c> 为空——车回显的就是空，照样能出发。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task HoldingCargoWithNothingLeftToLoadTheVehicleSimplyLeaves()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await LoadTheBasketAsync(fixture);
        await fixture.ProveSlotDoorsClosedAsync();
        JourneyRuntimeRow holding = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, holding.Stage);
        int worklistsBefore = await CountAsync(fixture, "CurrentStopWorklistSnapshot");

        string departureRequestId = await RequestDepartureAsync(fixture, operationSessionId: null);
        await RunAsync(fixture);

        Assert.Equal("ACCEPTED", (await ResultAsync(fixture, departureRequestId)).GetProperty("outcome").GetString());
        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(LoadingClosedReasons.OperatorDeparted, journey.LoadingClosedReason);
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, journey.Stage);
        Assert.Equal(worklistsBefore, await CountAsync(fixture, "CurrentStopWorklistSnapshot"));
        Assert.Null((await MembershipAsync(fixture, Demand)).RemovedAt);
    }

    /// <summary>
    /// 验收 3（含锚需求）：一篮没装时点出发：旅程原地收尾，锚需求在内全部放回积压（不抑制、不计次数），车不动——一张新的 RIoT 单都不建；
    /// 收尾清单说 <c>OPERATOR_DEPARTED</c>，车被放开，交给派车轮。
    /// </summary>
    /// <remarks>
    /// 这也是锚需求那一格在今天唯一到得了的样子：锚在第一个停靠，它没装时车上一篮都没有（见 <see cref="CurrentStopDepartureRule"/>）。
    /// 去掉一篮没装那一支放回锚（只放回别的），锚留在一趟已收尾的旅程上、不回积压，这一条红。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task WithNothingLoadedTheJourneyClosesInPlaceAndEveryDemandIncludingTheAnchorGoesBack()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        JourneyRuntimeRow waiting = await fixture.AdvanceToSublotWaitAsync();
        Assert.Equal(Demand, waiting.DemandId);
        await fixture.ProveSlotDoorsClosedAsync();
        int ordersBefore = await fixture.Context.OrderIntents.CountAsync(Token);

        string departureRequestId = await RequestDepartureAsync(fixture);
        await RunAsync(fixture);

        Assert.Equal("ACCEPTED", (await ResultAsync(fixture, departureRequestId)).GetProperty("outcome").GetString());
        JourneyRuntimeRow journey = await fixture.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == waiting.JourneyId, Token);
        Assert.Equal(JourneyRuntimeStage.Completed, journey.Stage);
        Assert.Equal(CurrentStopDepartures.OperatorDepartedReason, journey.BlockReasonCode);
        JourneyDemandRow anchor = await MembershipAsync(fixture, Demand);
        Assert.Equal(DemandJourneyLookup.ReleasedForRedispatchReason, anchor.RemovalReason);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
        Assert.True(
            await DemandJourneyLookup.ReleasedForRedispatch(fixture.Context).AnyAsync(row => row.DemandId == Demand, Token));
        await SuppressionAssertions.AssertNothingSuppressedAsync(fixture.Context);
        Assert.Equal(0, await StationDeadlineReleasesAsync(fixture));
        Assert.False(await fixture.Context.Set<VehiclePurposeClaimRow>().AnyAsync(Token), "The vehicle is still claimed.");
        Assert.Equal(ordersBefore, await fixture.Context.OrderIntents.CountAsync(Token));
        Assert.Contains("OPERATOR_DEPARTED", await StopEndedReasonsSentAsync(fixture));
    }

    /// <summary>验收 5、向量 STATION-MISMATCH：请求的站不是车所在的停靠，先于版本判；什么都不变。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-STATION-MISMATCH")]
    public async Task DepartureStationMismatchIsCheckedBeforeTheRevision()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await fixture.AdvanceToSublotWaitAsync();
        await fixture.ProveSlotDoorsClosedAsync();
        long current = await CurrentStopDepartures.CurrentWorklistRevisionAsync(fixture.Context, fixture.Options.AgvId, Token);

        // 站不对、版本也旧：答的是站不对。
        string departureRequestId = await RequestDepartureAsync(fixture, stationId: "N1-1", worklistRevision: current - 1);
        await RunAsync(fixture);

        await AssertRejectedAsync(fixture, departureRequestId, ServerReasonCodes.StationMismatch, current);
        await AssertUnchangedAsync(fixture);
    }

    /// <summary>验收 5、向量 STALE-REVISION：版本不是此刻的那一版，答复带上此刻的号；什么都不变。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-STALE-REVISION")]
    public async Task DepartureStaleRevisionReturnsTheCurrentRevision()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await fixture.AdvanceToSublotWaitAsync();
        await fixture.ProveSlotDoorsClosedAsync();
        long current = await CurrentStopDepartures.CurrentWorklistRevisionAsync(fixture.Context, fixture.Options.AgvId, Token);

        string departureRequestId = await RequestDepartureAsync(fixture, worklistRevision: current - 1);
        await RunAsync(fixture);

        await AssertRejectedAsync(fixture, departureRequestId, ServerReasonCodes.WorklistRevisionStale, current);
        await AssertUnchangedAsync(fixture);
    }

    /// <summary>
    /// 验收 7、向量 NOT-ALLOWED：机台取货停靠（规则开）与开关关着的待送点，出发请求一律 <c>ACTION_NOT_ALLOWED_IN_STATE</c>，什么都不变。
    /// 去掉待送点判别（<c>AtStagingPickup</c> 恒真），机台那一种红。
    /// </summary>
    [Theory]
    [InlineData(StopKind.MachineRulesOn)]
    [InlineData(StopKind.StagingRulesOff)]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-NOT-ALLOWED")]
    public async Task DepartureNotAllowedAwayFromAStagingPickup(StopKind kind)
    {
        await using RuntimeFixture fixture = await ArrangeAsync(kind);
        await fixture.AdvanceToSublotWaitAsync();
        await fixture.ProveSlotDoorsClosedAsync();
        long current = await CurrentStopDepartures.CurrentWorklistRevisionAsync(fixture.Context, fixture.Options.AgvId, Token);

        string departureRequestId = await RequestDepartureAsync(fixture);
        await RunAsync(fixture);

        await AssertRejectedAsync(fixture, departureRequestId, ServerReasonCodes.ActionNotAllowedInState, current);
        await AssertUnchangedAsync(fixture);
    }

    /// <summary>验收 5：正在装（装货命令已下、结果未到）时点出发被拒，那一篮照常装完。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-NOT-ALLOWED")]
    public async Task DepartureNotAllowedWhileALoadIsRunning()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await fixture.AdvanceToLoadResultAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.RuntimeAsync()).Stage);
        long current = await CurrentStopDepartures.CurrentWorklistRevisionAsync(fixture.Context, fixture.Options.AgvId, Token);

        string departureRequestId = await RequestDepartureAsync(fixture);
        await RunAsync(fixture);

        await AssertRejectedAsync(fixture, departureRequestId, ServerReasonCodes.ActionNotAllowedInState, current);
        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, journey.Stage);
        Assert.NotEqual(LoadingPhaseStates.Closed, journey.LoadingPhaseState);
        Assert.Null((await MembershipAsync(fixture, Demand)).RemovedAt);
    }

    /// <summary>验收 5：装完之后开着一条纠错时点出发被拒；纠错闭环之前车不走。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-NOT-ALLOWED")]
    public async Task DepartureNotAllowedWhileACorrectionIsOpen()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await LoadTheBasketAsync(fixture);
        await fixture.ProveSlotDoorsClosedAsync();
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            string answer = await fixture.RequestLoadCorrectionOnConnectionAsync(
                connection,
                Guid.NewGuid().ToString("D"),
                Demand,
                load.SlotOperationAttemptId,
                JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson)!);
            Assert.DoesNotContain("LoadCorrectionRejected", answer, StringComparison.Ordinal);
        }
        long current = await CurrentStopDepartures.CurrentWorklistRevisionAsync(fixture.Context, fixture.Options.AgvId, Token);

        string departureRequestId = await RequestDepartureAsync(fixture, operationSessionId: null);
        await RunAsync(fixture);

        await AssertRejectedAsync(fixture, departureRequestId, ServerReasonCodes.ActionNotAllowedInState, current);
        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.NotEqual(LoadingClosedReasons.OperatorDeparted, journey.LoadingClosedReason);
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture));
    }

    /// <summary>
    /// 验收 5、向量 DUPLICATE-REQUEST：同一个 <c>departureRequestId</c>、同一份内容再来（换了 messageId）：只判一次、只放回一次，答复是存下的那一行原样重放；
    /// 同号不同内容在入站以 <c>BUSINESS_ID_CONTENT_CONFLICT</c> 拒收，不进收件箱、不答 <c>CurrentStopDepartureResult</c>。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-CURRENT-STOP-DEPARTURE-DUPLICATE-REQUEST")]
    public async Task DepartureDuplicateRequestReplaysTheStoredResultAndRefusesOtherContent()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await LoadTheBasketAsync(fixture);
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        await fixture.ProveSlotDoorsClosedAsync();

        object payload = await DeparturePayloadAsync(fixture, Guid.NewGuid().ToString("D"));
        string departureRequestId = (string)payload.GetType().GetProperty("departureRequestId")!.GetValue(payload)!;
        await fixture.AddInboxAsync(Guid.NewGuid().ToString("D"), CurrentStopDepartures.RequestType, payload);
        await RunAsync(fixture);
        ProtocolOutboxRow first = await ResultRowAsync(fixture, departureRequestId);
        string releasedAt = (await MembershipAsync(fixture, SecondDemand)).RemovedAt!.Value.ToString("O");
        // 车确认过那条答复（向量第 4 步）。
        await fixture.Context.ProtocolOutbox.Where(row => row.MessageId == first.MessageId)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.AcknowledgedAt, fixture.Clock.GetUtcNow()), Token);

        // 同号同内容、新 messageId：经入站进来，应答之后原样重放存下的那一行；引擎不再判第二次。
        RecordingPeer wire = new();
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            string answer = await ProcessAsync(fixture, connection, wire, payload);
            Assert.Contains("\"DurableAck\"", answer, StringComparison.Ordinal);
        }
        await RunAsync(fixture);
        byte[] replayed = Assert.Single(wire.Lines);
        Assert.Equal(first.PayloadJson, System.Text.Encoding.UTF8.GetString(replayed).TrimEnd('\n'));
        Assert.Equal(1, await CountAsync(fixture, CurrentStopDepartures.ResultType));
        Assert.Equal(releasedAt, (await MembershipAsync(fixture, SecondDemand)).RemovedAt!.Value.ToString("O"));
        Assert.Equal(1, await Batch7CargoHoldingTests.DepartureChecksSentAsync(fixture));

        // 同号不同内容：拒收、连接保持，不进收件箱，也不多一条答复。
        int inboxBefore = await fixture.Context.ProtocolInbox.CountAsync(Token);
        object conflicting = await DeparturePayloadAsync(fixture, departureRequestId, operatorId: "OP-OTHER");
        await using (ControlServerDbContext connection = fixture.OpenConnectionContext())
        {
            string answer = await ProcessAsync(fixture, connection, new RecordingPeer(), conflicting);
            Assert.Contains("\"ProtocolProblem\"", answer, StringComparison.Ordinal);
            Assert.Contains(ServerReasonCodes.BusinessIdContentConflict, answer, StringComparison.Ordinal);
        }
        Assert.Equal(inboxBefore, await fixture.Context.ProtocolInbox.CountAsync(Token));
        Assert.Equal(1, await CountAsync(fixture, CurrentStopDepartures.ResultType));
    }

    /// <summary>
    /// hmi#349 审查转来的第 5 点：同一站的不同停靠，<c>worklistRevision</c> 会不会重复。不会——清单号按车单调（按车计数器给每趟旅程的基准，
    /// 同一趟里各停靠的号段首尾相接），所以第 1 个待送点停靠上的最后一版、到了第 2 个（同站追加新开的）停靠就是过时的：拿它点出发答
    /// <c>WORKLIST_REVISION_STALE</c>，不会被认成第 2 个停靠的出发。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ARevisionShownAtAnEarlierStopOfTheSameStationIsStaleAtTheNextOne()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await LoadTheBasketAsync(fixture);
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        object atFirstStop = await DeparturePayloadAsync(fixture, Guid.NewGuid().ToString("D"));
        long firstStopRevision = (long)atFirstStop.GetType().GetProperty("worklistRevision")!.GetValue(atFirstStop)!;
        fixture.Clock.Advance(fixture.Options.SameStationAppendCorrectionGrace + TimeSpan.FromSeconds(1));
        await fixture.ProveSlotDoorsClosedAsync();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        await Batch7StopDrivenAdvanceDriver.AnswerDepartureSafetyAsync(
            fixture, Demand, Batch7StopDrivenAdvanceDriver.FirstSafetyResultId);
        await Batch7StopDrivenAdvanceDriver.ArriveAtCurrentStopAsync(fixture, SecondDemand, "TO_GATE");
        await fixture.ProveSlotDoorsClosedAsync();
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        StopEntryAddress second = (await JourneyStopCursor.LoadAsync(fixture.Context, runtime, Token))
            .EntryAddressOfCurrentStop(runtime.WorklistRevision);
        Assert.Equal(StagingStationName, second.StationId);
        Assert.True(second.FirstWorklistRevision > firstStopRevision, $"{second.FirstWorklistRevision} vs {firstStopRevision}");

        // 同站、旧停靠的号：过时，不是第 2 个停靠的出发。
        await fixture.AddInboxAsync(Guid.NewGuid().ToString("D"), CurrentStopDepartures.RequestType, atFirstStop);
        await RunAsync(fixture);

        string departureRequestId = (string)atFirstStop.GetType().GetProperty("departureRequestId")!.GetValue(atFirstStop)!;
        JsonElement result = await ResultAsync(fixture, departureRequestId);
        Assert.Equal(ServerReasonCodes.WorklistRevisionStale, result.GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Null((await MembershipAsync(fixture, SecondDemand)).RemovedAt);
    }

    /// <summary>
    /// 车没有在途旅程时到的出发请求（旅程刚收尾）也要答：<c>STATION_MISMATCH</c>，由派车轮之前的扫尾答，否则车载端会一直挂着（hmi#349 联调第 2 条）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ARequestForAJourneyThatHasClosedIsStillAnswered()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await fixture.AdvanceToSublotWaitAsync();
        await fixture.ProveSlotDoorsClosedAsync();
        string first = await RequestDepartureAsync(fixture);
        object stale = await DeparturePayloadAsync(fixture, Guid.NewGuid().ToString("D"));
        await RunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.Context.JourneyRuntimes.AsNoTracking().SingleAsync(Token)).Stage);
        Assert.Equal("ACCEPTED", (await ResultAsync(fixture, first)).GetProperty("outcome").GetString());

        // 第二次按键在收尾之后才到（它回显的还是收尾之前那张清单）。
        await fixture.AddInboxAsync(Guid.NewGuid().ToString("D"), CurrentStopDepartures.RequestType, stale);
        long current = await CurrentStopDepartures.CurrentWorklistRevisionAsync(fixture.Context, fixture.Options.AgvId, Token);
        await RunAsync(fixture);

        string second = (string)stale.GetType().GetProperty("departureRequestId")!.GetValue(stale)!;
        await AssertRejectedAsync(fixture, second, ServerReasonCodes.StationMismatch, current);
    }

    /// <summary>
    /// 第 4 项（CP-0013）、向量 STAGING-CHARGING-REQUIRED-DEPARTURE-WITH-CARGO：待送点上装了一篮、同站还挂着一条没装，电量跌到强制充电线以下：
    /// 按自动出发处理——装货阶段以 <c>CHARGING_REQUIRED</c> 关闭，没装的那条放回待派（不抑制、不计次数），带着那一篮走正常离站；旅程里不插充电
    /// 停靠，用途仍是 TRANSPORT、充电周期仍是 NOT_CHARGING。没有出发请求，也就没有 <c>CurrentStopDepartureResult</c>。
    /// </summary>
    /// <remarks>
    /// 旅程收尾之后由充电分配接手，那是既有路径（一辆没有用途、电量在线下的空闲车），<see cref="StagingPickupNoCountdownTests"/> 的缺电 0 篮那一条
    /// 证过同一件事。机台取货的同一组事实什么都不做：<see cref="TheSameFlatBatteryAtAMachinePickupChangesNothing"/>。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-13")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    [Trait("ProtocolVector", "CV-STAGING-CHARGING-REQUIRED-DEPARTURE-WITH-CARGO")]
    public async Task ChargingRequiredWithCargoDeliversFirstAndChargesAfterwards()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await LoadTheBasketAsync(fixture);
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 10 };
        fixture.Riot.BeforeReadVehicle = () => fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));

        await RunAsync(fixture);

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal((LoadingPhaseStates.Closed, LoadingClosedReasons.ChargingRequired), (journey.LoadingPhaseState, journey.LoadingClosedReason));
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, journey.Stage);
        JourneyDemandRow released = await MembershipAsync(fixture, SecondDemand);
        Assert.Equal(DemandJourneyLookup.ReleasedForRedispatchReason, released.RemovalReason);
        Assert.Equal(DemandExecutionStatus.Accepted, (await DemandAsync(fixture, SecondDemand)).Status);
        await SuppressionAssertions.AssertNothingSuppressedAsync(fixture.Context);
        Assert.Equal(0, await StationDeadlineReleasesAsync(fixture));
        Assert.Equal((JourneyDemandStatuses.Loaded, (DateTimeOffset?)null),
            ((await MembershipAsync(fixture, Demand)).Status, (await MembershipAsync(fixture, Demand)).RemovedAt));
        Assert.Equal(0, await CountAsync(fixture, CurrentStopDepartures.ResultType));
        Assert.DoesNotContain(
            await fixture.Context.Set<JourneyStopRow>().AsNoTracking().Where(row => row.JourneyId == journey.JourneyId).ToArrayAsync(Token),
            stop => stop.StopRole is not (JourneyStopRoles.Pickup or JourneyStopRoles.Unload));

        string[] states = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "VehicleBusinessStateSnapshot")
            .Select(row => row.PayloadJson)
            .ToArrayAsync(Token);
        Assert.All(states, json =>
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement payload = document.RootElement.GetProperty("payload");
            Assert.Equal("TRANSPORT", payload.GetProperty("activePurpose").GetString());
            Assert.Equal("NOT_CHARGING", payload.GetProperty("chargingCycleState").GetString());
        });
        Assert.Contains(fixture.EngineLog.Entries, entry => entry.Message.Contains("delivers what it carries first", StringComparison.Ordinal));
    }

    /// <summary>同一组事实放在机台取货停靠上（规则开）：什么都不做，车照旧等录入（机台取货不变的回归）。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-13")]
    public async Task TheSameFlatBatteryAtAMachinePickupChangesNothing()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.MachineRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await LoadTheBasketAsync(fixture);
        await fixture.ProveSlotDoorsClosedAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 10 };
        fixture.Riot.BeforeReadVehicle = () => fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));

        await RunAsync(fixture);

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.NotEqual(LoadingClosedReasons.ChargingRequired, journey.LoadingClosedReason);
        Assert.Equal(before.Stage, journey.Stage);
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry => entry.Message.Contains("delivers what it carries first", StringComparison.Ordinal));
    }

    /// <summary>
    /// 规则本身的先后（<see cref="CurrentStopDepartureRule"/>）：站 → 版本 → 状态；锚需求没装而车上有货答不允许（纵深防御，见规则注释）。
    /// 去掉锚那一条，最后一个断言红。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public void TheRuleChecksStationThenRevisionThenState()
    {
        CurrentStopDepartures.Request request = new(
            "m", "AGV", "d", "SESSION", "305", 7, "OP", DateTimeOffset.UnixEpoch);
        CurrentStopDepartureRule.Facts ok = new("305", "SESSION", 7, true, true, false, false, false, false, false);

        Assert.Null(CurrentStopDepartureRule.Decide(request, ok));
        Assert.Null(CurrentStopDepartureRule.Decide(request with { OperationSessionId = null }, ok));
        Assert.Equal(ServerReasonCodes.StationMismatch,
            CurrentStopDepartureRule.Decide(request with { StationId = "12", WorklistRevision = 3 }, ok with { AtStagingPickup = false })!.ReasonCode);
        Assert.Equal(ServerReasonCodes.StationMismatch, CurrentStopDepartureRule.Decide(request, ok with { CurrentStationId = null })!.ReasonCode);
        Assert.Equal(ServerReasonCodes.WorklistRevisionStale,
            CurrentStopDepartureRule.Decide(request with { WorklistRevision = 6 }, ok with { SlotOperationRunning = true })!.ReasonCode);
        Assert.Equal(ServerReasonCodes.WorklistRevisionStale,
            CurrentStopDepartureRule.Decide(request with { OperationSessionId = "OTHER" }, ok)!.ReasonCode);
        foreach (CurrentStopDepartureRule.Facts refused in new[]
                 {
                     ok with { AtStagingPickup = false },
                     ok with { AtStopAwaitingDeparture = false },
                     ok with { SlotOperationRunning = true },
                     ok with { CorrectionOrCancellationOpen = true },
                     ok with { AlreadyDeparting = true },
                     ok with { NothingLoadedAndDoorsNotProvenClosed = true },
                     ok with { AnchorUnloadedWithCargo = true },
                 })
        {
            Assert.Equal(ServerReasonCodes.ActionNotAllowedInState, CurrentStopDepartureRule.Decide(request, refused)!.ReasonCode);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    internal static async Task<object> DeparturePayloadAsync(
        RuntimeFixture fixture,
        string departureRequestId,
        string? stationId = null,
        long? worklistRevision = null,
        string? operationSessionId = "current",
        string operatorId = "OP-713")
    {
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        JourneyStopRow current = (await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
                .Where(row => row.JourneyId == runtime.JourneyId)
                .ToArrayAsync(Token))
            .Where(row => row.Status != JourneyStopStatuses.Completed && row.Status != JourneyStopStatuses.Removed)
            .OrderBy(row => row.Sequence)
            .First();
        return new
        {
            departureRequestId,
            operationSessionId = operationSessionId == "current" ? current.OperationSessionId : operationSessionId,
            stationId = stationId ?? current.StationId,
            worklistRevision = worklistRevision ??
                               await CurrentStopDepartures.CurrentWorklistRevisionAsync(fixture.Context, fixture.Options.AgvId, Token),
            @operator = new { operatorId, verificationMethod = "BADGE", verifiedAt = fixture.Clock.GetUtcNow() }
        };
    }

    /// <summary>车上点一次出发：请求落进收件箱（入站只回 DurableAck），返回 departureRequestId。</summary>
    internal static async Task<string> RequestDepartureAsync(
        RuntimeFixture fixture,
        string? stationId = null,
        long? worklistRevision = null,
        string? operationSessionId = "current")
    {
        string departureRequestId = Guid.NewGuid().ToString("D");
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            CurrentStopDepartures.RequestType,
            await DeparturePayloadAsync(fixture, departureRequestId, stationId, worklistRevision, operationSessionId));
        return departureRequestId;
    }

    internal static async Task RunAsync(RuntimeFixture fixture)
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
    }

    private static async Task<string> ProcessAsync(
        RuntimeFixture fixture,
        ControlServerDbContext connection,
        RecordingPeer wire,
        object payload)
    {
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), wire);
        OnboardConnectionState state = new()
        {
            AgvId = fixture.Options.AgvId,
            SessionGeneration = 1,
            CapabilityRevision = 1,
            SafetyRevision = 7,
            Readiness = SessionReadiness.Ready,
            HandshakeCompleted = true
        };
        string line = JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType = CurrentStopDepartures.RequestType,
            messageId = Guid.NewGuid().ToString("D"),
            correlationId = (string?)null,
            agvId = fixture.Options.AgvId,
            sessionGeneration = 1L,
            sentAt = fixture.Clock.GetUtcNow(),
            payload
        }, SerializerOptions);
        return await processor.ProcessAsync(line, state, Token);
    }

    internal static async Task<ProtocolOutboxRow> ResultRowAsync(RuntimeFixture fixture, string departureRequestId)
    {
        string id = CurrentStopDepartures.ResultMessageId(departureRequestId);
        return await fixture.Context.ProtocolOutbox.AsNoTracking().SingleAsync(row => row.MessageId == id, Token);
    }

    internal static async Task<JsonElement> ResultAsync(RuntimeFixture fixture, string departureRequestId)
    {
        using JsonDocument document = JsonDocument.Parse((await ResultRowAsync(fixture, departureRequestId)).PayloadJson);
        return document.RootElement.GetProperty("payload").Clone();
    }

    private static async Task AssertRejectedAsync(RuntimeFixture fixture, string departureRequestId, string reasonCode, long currentRevision)
    {
        JsonElement result = await ResultAsync(fixture, departureRequestId);
        Assert.Equal("REJECTED", result.GetProperty("outcome").GetString());
        Assert.Equal(reasonCode, result.GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Equal(currentRevision, result.GetProperty("currentWorklistRevision").GetInt64());
    }

    /// <summary>被拒的出发什么业务状态都不改：需求、归属、装货阶段与阶段照旧，没有放回，没有抑制，没发离站核验。</summary>
    private static async Task AssertUnchangedAsync(RuntimeFixture fixture)
    {
        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, journey.Stage);
        Assert.NotEqual(LoadingPhaseStates.Closed, journey.LoadingPhaseState);
        Assert.Null((await MembershipAsync(fixture, Demand)).RemovedAt);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
        await SuppressionAssertions.AssertNothingSuppressedAsync(fixture.Context);
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture));
    }

    private static Task<JourneyDemandRow> MembershipAsync(RuntimeFixture fixture, string demandId) =>
        fixture.Context.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(row => row.DemandId == demandId, Token);

    private static Task<AcceptedDemandRow> DemandAsync(RuntimeFixture fixture, string demandId) =>
        fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(row => row.DemandId == demandId, Token);

    private static Task<int> StationDeadlineReleasesAsync(RuntimeFixture fixture) =>
        fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .CountAsync(row => row.RemovalReason == DemandJourneyLookup.ReleasedAtStationDeadlineReason, Token);

    private static Task<int> CountAsync(RuntimeFixture fixture, string messageType) =>
        fixture.Context.ProtocolOutbox.AsNoTracking().CountAsync(row => row.MessageType == messageType, Token);

    internal static async Task<string?[]> StopEndedReasonsSentAsync(RuntimeFixture fixture)
    {
        string[] payloads = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "CurrentStopWorklistSnapshot")
            .Select(row => row.PayloadJson)
            .ToArrayAsync(Token);
        return
        [
            .. payloads.Select(json =>
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement reason = document.RootElement.GetProperty("payload").GetProperty("stopEndedReason");
                return reason.ValueKind == JsonValueKind.Null ? null : reason.GetString();
            })
        ];
    }

    private static string MessageTypeOf(byte[] line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("messageType").GetString()!;
    }
}
