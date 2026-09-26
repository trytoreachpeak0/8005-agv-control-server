using System.Data.Common;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.ClosureSnapshotAssertions;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 一站结束而旅程继续（program#86 v2 的 B 形态，control-server#324）：车上不能留着这一站。
/// </summary>
/// <remarks>
/// <para>
/// <b>形状。</b>一趟两个取货站、一个卸货站的旅程，第一条需求在第一站装上车，第二个取货站等录入时离站期限到期。
/// 期限终结了这一站的那条需求，但车上还有第一站的货，旅程不收尾，改走离站等待（批次7-07），还可能持货等单（REQ-0354）
/// 在这一站停很久。收尾快照（control-server#323）只在旅程收尾时发，这里一张都不发。
/// </para>
/// <para>
/// <b>修之前车上看到的。</b>清单还列着被终结的那条、录入请求还在、「取消装货」按钮还在；按取消得到的是
/// <c>ACTION_NOT_ALLOWED_IN_STATE</c>，扫码只得到 <c>DurableAck</c>、永远没人回答。
/// </para>
/// <para>
/// <b>号数是这里最容易出错的地方。</b>一站 N 条需求发 N 版清单，做完最后一条按算式恰好等于<b>下一站的首号</b>。在那个号上发一张空清单，
/// 下一站到站时同号不同内容，车当场以 <c>SNAPSHOT_REVISION_CONTENT_CONFLICT</c> 断会话。所以用例一路跑到下一站，断的是整条清单流
/// 的号互不相同、严格递增。
/// </para>
/// </remarks>
public sealed class StopEndedJourneyContinuesTests
{
    /// <summary>第二条需求的 AREA，解析到站 13，与第一条的站 12 不同（同 <see cref="Batch7ThreeStopJourneyTests"/>）。</summary>
    private const string SecondPickupArea = "N1-2";

    private const string WorklistType = "CurrentStopWorklistSnapshot";

    /// <summary>
    /// 第二个取货站被期限结束、旅程继续：车收到一张这一站的空清单（号比这条流上已发的都大、作业会话与期限为 null），
    /// 车上那一版清单在发件箱里退役，这一站的录入请求被结清。
    /// </summary>
    /// <remarks>
    /// 「退役」与「结清」断的是重连之后的样子：没退役的旧清单会被补发，号比空清单低，车判回退而断会话；没结清的录入请求被补发，
    /// 车上又亮起扫码入口。空清单本身断的是<b>线上那一行</b>，不只是发件箱。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnEndedStopTheJourneyLeavesWithCargoSendsTheVehicleAnEmptyWorklist()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        JourneyStopRow secondPickup = await EndTheSecondPickupByItsDeadlineAsync(fixture);

        Snapshot[] worklists = await WorklistsAsync(fixture);
        Snapshot empty = Assert.Single(worklists, item => item.Payload.GetProperty("items").GetArrayLength() == 0);
        Assert.Equal(secondPickup.StationId, empty.Payload.GetProperty("stationId").GetString());
        Assert.Equal(JsonValueKind.Null, empty.Payload.GetProperty("operationSessionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.Payload.GetProperty("stationDepartureDeadlineAt").ValueKind);
        Assert.All(
            worklists.Where(item => item.MessageId != empty.MessageId),
            other => Assert.True(
                empty.Revision > other.Revision,
                $"空清单是第 {empty.Revision} 号，没有越过 {other.MessageId} 的第 {other.Revision} 号。"));
        Assert.Contains(fixture.Peer.Lines, bytes => SentAs(Line(bytes), empty.MessageId, 1));

        // 这一站此前发给车的每一版清单都退役（或早已确认），重连后不会被补发。
        string[] earlierAtThisStop =
        [
            .. worklists
                .Where(item => item.MessageId != empty.MessageId &&
                               item.Payload.GetProperty("stationId").GetString() == secondPickup.StationId)
                .Select(item => item.MessageId)
        ];
        Assert.NotEmpty(earlierAtThisStop);
        Assert.All(
            await fixture.Context.ProtocolOutbox.AsNoTracking()
                .Where(row => earlierAtThisStop.Contains(row.MessageId))
                .ToArrayAsync(token),
            row => Assert.True(
                row.FencedAt is not null || row.AcknowledgedAt is not null,
                $"这一站较早那一版清单 {row.MessageId} 还会被补发。"));

        // 这一站的录入请求结清了：没人会再答它。
        ProtocolOutboxRow entryRequest = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageId == secondPickup.SublotRequestMessageId, token);
        Assert.NotNull(entryRequest.AcknowledgedAt);

        // 旅程没有被错关：车上那批货还在计划里。
        JourneyRuntimeRow after = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.NotEqual(JourneyRuntimeStage.Completed, after.Stage);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, FirstDemandId)).Status);
    }

    /// <summary>
    /// 空清单之后车离站去卸货站：卸货站那一版清单的号在空清单之上，整条清单流的号互不相同、严格递增。
    /// </summary>
    /// <remarks>
    /// 它断的是结果（号在空清单之上、全程不同号），<b>守不住「为什么」</b>：这里甲、乙共用一个卸货站，卸货站把已终结的乙也算作「做完」，
    /// 不顺延号也自然多出一号。票面那句「同号不同内容会当场断会话」由下一条用例守，那里下一站不挂任何已终结的需求。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheNextStopsWorklistStartsAboveTheEmptyOne()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await EndTheSecondPickupByItsDeadlineAsync(fixture);
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, SecondSafetyResultId);
        JourneyRuntimeRow atGate = await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_GATE");
        JourneyStopRow unloadStop = await CurrentStopAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyStopRoles.Unload, unloadStop.StopRole);

        Snapshot[] worklists = await WorklistsAsync(fixture);
        Snapshot empty = Assert.Single(worklists, item => item.Payload.GetProperty("items").GetArrayLength() == 0);
        Snapshot atUnload = Assert.Single(
            worklists, item => item.Payload.GetProperty("stationId").GetString() == unloadStop.StationId);
        Assert.True(
            atUnload.Revision > empty.Revision,
            $"卸货站清单第 {atUnload.Revision} 号没有越过空清单的第 {empty.Revision} 号（stage={atGate.Stage}）。");
        long[] revisions = [.. worklists.Select(item => item.Revision)];
        Assert.Equal(revisions.Distinct().Count(), revisions.Length);
    }

    /// <summary>
    /// 同上，但被终结的乙<b>有自己的卸货停靠</b>：终结之后那个停靠被计划删掉，车下一站是只挂着甲的卸货站。
    /// 那一站的首号按算式恰好是「做完最后一条」那个号——不顺延，空清单就与它同号不同内容。
    /// </summary>
    /// <remarks>
    /// 上一条用例守不住这件事，是反向验证（去掉顺延）时看出来的：那里甲、乙共用一个卸货站，卸货站把已终结的乙也算作「做完」，
    /// 号自然多出一号，碰巧躲开了冲突。这里让下一站一条已终结的都不挂，冲突就只能靠顺延来避。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task WhenTheNextStopCarriesNoneOfTheEndedDemandsItStillStartsAboveTheEmptyWorklist()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await EndTheSecondPickupByItsDeadlineAsync(fixture, secondUnloadsAtItsOwnStop: true);
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, SecondSafetyResultId);
        await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_GATE");
        JourneyStopRow unloadStop = await CurrentStopAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyStopRoles.Unload, unloadStop.StopRole);
        Assert.NotEqual(SecondUnloadStopId(await JourneyOfAsync(fixture, FirstDemandId)), unloadStop.StopId);

        Snapshot[] worklists = await WorklistsAsync(fixture);
        Snapshot empty = Assert.Single(worklists, item => item.Payload.GetProperty("items").GetArrayLength() == 0);
        Snapshot atUnload = Assert.Single(worklists, item => item.Payload.GetProperty("items").GetArrayLength() > 0 &&
                                                          item.Payload.GetProperty("stationId").GetString() == unloadStop.StationId);
        Assert.True(
            atUnload.Revision > empty.Revision,
            $"卸货站清单第 {atUnload.Revision} 号没有越过空清单的第 {empty.Revision} 号。");
        long[] revisions = [.. worklists.Select(item => item.Revision)];
        Assert.Equal(revisions.Distinct().Count(), revisions.Length);
    }

    /// <summary>
    /// 同一站<b>先</b>因断线重连重填了离站期限（cs#339，清单升一版），<b>再</b>被期限结束：两种「本停靠多占一版」叠在
    /// <see cref="JourneyStopRow.WorklistRefills"/> 上，号仍然严格递增、互不相同，空清单在重填那一版之上，下一站在空清单之上。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 号怎么走：第二个取货站首号记为 F，一条需求。到站发 F；重填之后 <c>WorklistRefills</c> = 1，发 F+1（新期限）；期限结束时这一条算做完，
    /// 算式给 F+1+1 = F+2，空清单就是 F+2，<c>WorklistRefills</c> 再加一到 2，这一站占 F..F+2 三版，下一站首号 F+3 起算。
    /// </para>
    /// <para>
    /// 反过来的顺序——先空清单、再重填——同一站上不会发生：重填只在本停靠还有待做项时升版
    /// （<c>AdvanceWorklistPastAStaleDeadlineAsync</c> 开头的判断），而空清单正是在待做项归零时才发。结束之后断线重连，补发的是空清单本身，
    /// 由 <see cref="AfterAReconnectTheEmptyWorklistIsReplayedAndTheOldOneIsNot"/> 守。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ARefillBeforeTheStopEndsAndTheEmptyWorklistStackAndTheRevisionsStillOnlyAdvance()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        JourneyStopRow secondPickup = await EndTheSecondPickupByItsDeadlineAsync(fixture, refillFirst: true);

        Snapshot[] atStop = [.. (await WorklistsAsync(fixture))
            .Where(item => item.Payload.GetProperty("stationId").GetString() == secondPickup.StationId)
            .OrderBy(item => item.Revision)];
        // 到站那一版、重填那一版（同一条需求、新期限）、空清单：三版，连号。
        Assert.Equal(3, atStop.Length);
        Assert.Equal([atStop[0].Revision, atStop[0].Revision + 1, atStop[0].Revision + 2], atStop.Select(item => item.Revision).ToArray());
        Assert.Equal(0, atStop[2].Payload.GetProperty("items").GetArrayLength());
        Assert.NotEqual(
            atStop[0].Payload.GetProperty("stationDepartureDeadlineAt").GetString(),
            atStop[1].Payload.GetProperty("stationDepartureDeadlineAt").GetString());

        // 下一站首号按服务端自己的算式读，不把车开过去：重连之后是第 2 代会话，测试驱动器的离站答复写死第 1 代，会被当成旧会话的报文。
        // 开过去验证「真的发出了那一号」由上面几条用例在第 1 代上守。
        JourneyRuntimeRow runtime = await JourneyOfAsync(fixture, FirstDemandId);
        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(fixture.Context, runtime, token);
        JourneyStopRow next = stops.Stops.First(stop => stop.Sequence > secondPickup.Sequence &&
                                                        stop.Status != JourneyStopStatuses.Removed);
        long nextFirst = stops.FirstWorklistRevisionAt(runtime.WorklistRevision, next);
        Assert.True(
            nextFirst > atStop[2].Revision,
            $"下一站首号 {nextFirst} 没有越过空清单的第 {atStop[2].Revision} 号。");
        long[] revisions = [.. (await WorklistsAsync(fixture)).Select(item => item.Revision)];
        Assert.Equal(revisions.Distinct().Count(), revisions.Length);

        // 两种多占的版都记在同一列上：重填一次、空清单一次。放在最后，是为了让上面那条行为判据先说话。
        JourneyStopRow afterEnd = await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(row => row.StopId == secondPickup.StopId, token);
        Assert.Equal(2, afterEnd.WorklistRefills);
    }

    /// <summary>
    /// 空清单没确认就断线：重连之后补发的是空清单，不是这一站较早那一版。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AfterAReconnectTheEmptyWorklistIsReplayedAndTheOldOneIsNot()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyStopRow secondPickup = await EndTheSecondPickupByItsDeadlineAsync(fixture);
        Snapshot[] worklists = await WorklistsAsync(fixture);
        Snapshot empty = Assert.Single(worklists, item => item.Payload.GetProperty("items").GetArrayLength() == 0);
        string[] earlierAtThisStop =
        [
            .. worklists
                .Where(item => item.MessageId != empty.MessageId &&
                               item.Payload.GetProperty("stationId").GetString() == secondPickup.StationId)
                .Select(item => item.MessageId)
        ];

        await fixture.ReconnectAsync(2);
        await fixture.AdvanceSessionAsync(2);
        await fixture.HearFromPeerAsync();
        int before = fixture.Peer.Lines.Count;
        await TickAndRunAsync(fixture);

        string[] replayed = [.. fixture.Peer.Lines.Skip(before).Select(Line)];
        Assert.Contains(replayed, line => SentAs(line, empty.MessageId, 2));
        Assert.DoesNotContain(replayed, line => earlierAtThisStop.Any(id => SentAs(line, id, 2)));
    }

    /// <summary>
    /// 这一站结束之后才到的扫码：服务端答 <c>SublotRejected</c> / <c>WORKLIST_REVISION_STALE</c>，<c>demandId</c> 为 null，
    /// 修订号是空清单那一号，而且发到了线上。
    /// </summary>
    /// <remarks>
    /// 修之前它只得到 <c>DurableAck</c>：引擎只在 <c>AwaitingSublot</c> 里读录入，这一站已经离开那个阶段，车停在「已提交」。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ASublotEnteredAfterTheStopEndedIsRejectedAsStale()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyStopRow secondPickup = await EndTheSecondPickupByItsDeadlineAsync(fixture);
        JourneyRuntimeRow runtime = await JourneyOfAsync(fixture, FirstDemandId);
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer);

        const string submissionId = "d1000000-0000-4000-8000-000000000001";
        string response = await processor.ProcessAsync(
            Envelope(fixture, submissionId, "SublotSubmitted", new
            {
                operationSessionId = secondPickup.OperationSessionId,
                stationId = secondPickup.StationId,
                worklistRevision = runtime.WorklistRevision + 1,
                sublot = SecondSublot,
                entryMethod = "SCANNER",
                @operator = Operator(fixture)
            }),
            Connection(fixture),
            token);

        Assert.Equal("DurableAck", FirstLineType(response));
        ProtocolOutboxRow[] rejections = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "SublotRejected")
            .ToArrayAsync(token);
        ProtocolOutboxRow rejection = Assert.Single(rejections);
        using JsonDocument document = JsonDocument.Parse(rejection.PayloadJson);
        JsonElement root = document.RootElement;
        Assert.Equal(submissionId, root.GetProperty("correlationId").GetString());
        JsonElement payload = root.GetProperty("payload");
        Assert.Equal("WORKLIST_REVISION_STALE", payload.GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("demandId").ValueKind);
        Assert.Equal(SecondSublot, payload.GetProperty("rejectedSublot").GetString());
        Assert.Contains(fixture.Peer.Lines, bytes => SentAs(Line(bytes), rejection.MessageId, 1));
        // 号告诉车「此刻有效的是哪一版」：就是刚发的那张空清单。
        Snapshot empty = Assert.Single(
            await WorklistsAsync(fixture), item => item.Payload.GetProperty("items").GetArrayLength() == 0);
        Assert.Equal(empty.Revision, payload.GetProperty("currentWorklistRevision").GetInt64());
    }

    /// <summary>
    /// 这一站结束之后才按的「取消装货（扫码前）」：拒绝的原因码是 <c>WORKLIST_REVISION_STALE</c>，不再是误导操作员去查授权的
    /// <c>ACTION_NOT_ALLOWED_IN_STATE</c>。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ACancellationAfterTheStopEndedIsRejectedAsStale()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await EndTheSecondPickupByItsDeadlineAsync(fixture);
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build());

        string response = await processor.ProcessAsync(
            Envelope(fixture, "d1000000-0000-4000-8000-000000000002", "LoadCancellationStartRequested", new
            {
                cancellationId = "d1000000-0000-4000-8000-000000000003",
                demandId = SecondDemandId,
                slotOperationAttemptId = (string?)null,
                @operator = Operator(fixture),
                reason = "Nothing to load at this stop."
            }),
            Connection(fixture),
            token);

        JsonElement payload = FirstLinePayload(response);
        Assert.Equal("REJECTED", payload.GetProperty("decision").GetString());
        Assert.Equal("WORKLIST_REVISION_STALE", payload.GetProperty("problem").GetProperty("reasonCode").GetString());
    }

    /// <summary>
    /// 第二个取货站被<b>扫码前取消</b>结束（不是期限）：协调器当场把空清单发上线，阶段带着 0 个待做项仍是等录入；这时来的迟到扫码
    /// 答 <c>WORKLIST_REVISION_STALE</c>，号就是那张空清单的号（PR #361 两路审查的必修）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 修之前入站那一侧看到阶段是等录入就交给引擎，引擎按地址区间认出它，却在待做项里找不到，答 <c>SUBLOT_NOT_IN_DISPATCH_SCOPE</c>，
    /// 号是车从没收到过的 r+1——车载端显示「子批号不在本次派车范围内」。先前「那一行是多余防御」的判断只走了期限那一路，而期限会把阶段推走。
    /// </para>
    /// <para>
    /// 空清单在<b>结果那条报文的应答之后、引擎任何一轮之前</b>就上线：断的是协调器当场发送，不是引擎下一轮补发。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AfterACancellationEndsTheStopALateEntryIsRejectedAsStaleAtTheEmptyWorklistsRevision()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyStopRow secondPickup = await ArriveAtTheSecondPickupAsync(fixture);
        JourneyRuntimeRow runtime = await JourneyOfAsync(fixture, FirstDemandId);
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer);
        OnboardConnectionState state = Connection(fixture);
        const string cancellationId = "d2000000-0000-4000-8000-000000000001";

        string authorization = await processor.ProcessAsync(
            Envelope(fixture, "d2000000-0000-4000-8000-000000000002", "LoadCancellationStartRequested", new
            {
                cancellationId,
                demandId = SecondDemandId,
                slotOperationAttemptId = (string?)null,
                @operator = Operator(fixture),
                reason = "Nothing to load at this stop."
            }),
            state,
            token);
        Assert.Equal("AUTHORIZED", FirstLinePayload(authorization).GetProperty("decision").GetString());
        int linesBeforeResult = fixture.Peer.Lines.Count;
        await processor.ProcessAsync(
            Envelope(fixture, "d2000000-0000-4000-8000-000000000003", "LoadCancellationResult", new
            {
                cancellationId,
                demandId = SecondDemandId,
                slotOperationAttemptId = (string?)null,
                overallOutcome = "ALL_EMPTY",
                slotResults = Array.Empty<object>(),
                observedAt = Now
            }),
            state,
            token);

        // 前提：乙被取消终结，旅程继续，阶段仍是等录入——审查说的那个形状。
        Assert.Equal(JourneyDemandStatuses.Terminated, (await MembershipAsync(fixture, SecondDemandId)).Status);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        Snapshot empty = Assert.Single(
            await WorklistsAsync(fixture), item => item.Payload.GetProperty("items").GetArrayLength() == 0);
        Assert.Equal(secondPickup.StationId, empty.Payload.GetProperty("stationId").GetString());
        Assert.Contains(fixture.Peer.Lines.Skip(linesBeforeResult), bytes => SentAs(Line(bytes), empty.MessageId, 1));

        const string lateId = "d2000000-0000-4000-8000-000000000004";
        await processor.ProcessAsync(
            Envelope(fixture, lateId, "SublotSubmitted", new
            {
                operationSessionId = secondPickup.OperationSessionId,
                stationId = secondPickup.StationId,
                worklistRevision = runtime.WorklistRevision + 1,
                sublot = SecondSublot,
                entryMethod = "SCANNER",
                @operator = Operator(fixture)
            }),
            state,
            token);
        JsonElement rejection = await RejectionOfAsync(fixture, lateId);
        Assert.Equal("WORKLIST_REVISION_STALE", rejection.GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Equal(empty.Revision, rejection.GetProperty("currentWorklistRevision").GetInt64());

        // 引擎之后再转一轮也不会补答另一条：它认得这条录入已被拒收。
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        Assert.Single(await RejectionsOfAsync(fixture, lateId));
    }

    /// <summary>
    /// 扫码前取消已授权、结果还没到时落了一条扫码（这一站还有待做项，此刻不答它）；车随后报 ALL_EMPTY，本站结束——那条扫码在
    /// 同一次改动里被答 <c>WORKLIST_REVISION_STALE</c>，号是空清单的号，并上线（PR #361 增量复核：输给扫码前取消的在途扫码）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnEntryThatLosesToAnAuthorizedCancellationIsAnsweredStaleWhenTheResultEndsTheStop()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyStopRow secondPickup = await ArriveAtTheSecondPickupAsync(fixture);
        JourneyRuntimeRow runtime = await JourneyOfAsync(fixture, FirstDemandId);
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer);
        OnboardConnectionState state = Connection(fixture);
        const string cancellationId = "d3000000-0000-4000-8000-000000000001";
        string authorization = await processor.ProcessAsync(CancellationRequest(fixture, cancellationId), state, token);
        Assert.Equal("AUTHORIZED", FirstLinePayload(authorization).GetProperty("decision").GetString());

        const string entryId = "d3000000-0000-4000-8000-000000000004";
        await processor.ProcessAsync(
            Envelope(fixture, entryId, "SublotSubmitted", SecondStopEntry(fixture, secondPickup, runtime)), state, token);
        Assert.Empty(await RejectionsOfAsync(fixture, entryId));

        await processor.ProcessAsync(CancellationResult(fixture, cancellationId), state, token);

        Assert.Equal(JourneyDemandStatuses.Terminated, (await MembershipAsync(fixture, SecondDemandId)).Status);
        JsonElement rejection = await RejectionOfAsync(fixture, entryId);
        Assert.Equal("WORKLIST_REVISION_STALE", rejection.GetProperty("problem").GetProperty("reasonCode").GetString());
        Snapshot empty = Assert.Single(
            await WorklistsAsync(fixture), item => item.Payload.GetProperty("items").GetArrayLength() == 0);
        Assert.Equal(empty.Revision, rejection.GetProperty("currentWorklistRevision").GetInt64());
        Assert.Contains(
            fixture.Peer.Lines, bytes => SentAs(Line(bytes), LateSublotSubmission.RejectionMessageId(entryId), 1));
    }

    /// <summary>
    /// 同一个在途扫码，但引擎已经在锁外读到了它：读完收件箱之后 ALL_EMPTY 才落定、结束本站（本站结束那一次改动已为它暂存 STALE），
    /// 引擎接着按旧游标判它不在范围。这一轮<b>不抛</b>，这条扫码只有一条答复、仍是 STALE（PR #361 增量复核的必修）。
    /// </summary>
    /// <remarks>
    /// 修之前引擎用同一个派生 id 写一份内容不同的 <c>SUBLOT_NOT_IN_DISPATCH_SCOPE</c> 拒收，发件箱的重放校验抛
    /// <c>ProtocolContentConflictException</c>，推进段没有逐车隔离，这一轮对所有车 fail-closed。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task WhenTheCancellationEndsTheStopAfterTheRuntimeReadTheEntryTheRoundDoesNotThrowAndAnswersOnce()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        // 在引擎认定「这条录入没被拒收、没被消费」之后才让结果落定：早一步（例如读收件箱那一刻）引擎会接着读到 STALE 拒收而跳过，
        // 走不到写拒收，这条用例就什么都没测（第一版就是这样，去掉修复的变异 M11 存活）。
        EntryInterleaver interleaver = new(EntryInterleaver.AfterTheEntryWasJudgedUnanswered);
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: interleaver);
        JourneyStopRow secondPickup = await ArriveAtTheSecondPickupAsync(fixture);
        JourneyRuntimeRow runtime = await JourneyOfAsync(fixture, FirstDemandId);
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new ConfigurationBuilder().Build(), fixture.Peer);
        OnboardConnectionState state = Connection(fixture);
        const string cancellationId = "d4000000-0000-4000-8000-000000000001";
        string authorization = await processor.ProcessAsync(CancellationRequest(fixture, cancellationId), state, token);
        Assert.Equal("AUTHORIZED", FirstLinePayload(authorization).GetProperty("decision").GetString());
        // 扫的是一个不在派车范围里的子批（扫错了）：引擎按旧游标判它 SUBLOT_NOT_IN_DISPATCH_SCOPE，这正是会与 STALE 撞 id 的那一种。
        // 扫的若是乙本身，旧游标里乙仍待装，引擎走的是装货而不是拒收——那是另一个问题，另报（见 PR 正文）。
        const string entryId = "d4000000-0000-4000-8000-000000000004";
        await processor.ProcessAsync(
            Envelope(fixture, entryId, "SublotSubmitted", SecondStopEntry(fixture, secondPickup, runtime, "SUBLOT-NOT-HERE")),
            state,
            token);
        Assert.Empty(await RejectionsOfAsync(fixture, entryId));

        // 引擎读完收件箱的那一刻，车报 ALL_EMPTY、本站结束。
        interleaver.Arm(() => processor.ProcessAsync(CancellationResult(fixture, cancellationId), state, token));
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);

        Assert.Equal(1, interleaver.Fired);
        Assert.Equal(JourneyDemandStatuses.Terminated, (await MembershipAsync(fixture, SecondDemandId)).Status);
        JsonElement rejection = await RejectionOfAsync(fixture, entryId);
        Assert.Equal("WORKLIST_REVISION_STALE", rejection.GetProperty("problem").GetProperty("reasonCode").GetString());
    }

    private static string CancellationRequest(RuntimeFixture fixture, string cancellationId) =>
        Envelope(fixture, JourneyPlanBuilder.StableGuid(cancellationId, "request"), "LoadCancellationStartRequested", new
        {
            cancellationId,
            demandId = SecondDemandId,
            slotOperationAttemptId = (string?)null,
            @operator = Operator(fixture),
            reason = "Nothing to load at this stop."
        });

    private static string CancellationResult(RuntimeFixture fixture, string cancellationId) =>
        Envelope(fixture, JourneyPlanBuilder.StableGuid(cancellationId, "result"), "LoadCancellationResult", new
        {
            cancellationId,
            demandId = SecondDemandId,
            slotOperationAttemptId = (string?)null,
            overallOutcome = "ALL_EMPTY",
            slotResults = Array.Empty<object>(),
            observedAt = Now
        });

    /// <summary>对第二个取货站此刻那一版清单的一条录入，扫的是乙的子批。</summary>
    private static object SecondStopEntry(
        RuntimeFixture fixture, JourneyStopRow secondPickup, JourneyRuntimeRow runtime, string sublot = SecondSublot) => new
    {
        operationSessionId = secondPickup.OperationSessionId,
        stationId = secondPickup.StationId,
        worklistRevision = runtime.WorklistRevision + 1,
        sublot,
        entryMethod = "SCANNER",
        @operator = Operator(fixture)
    };

    /// <summary>
    /// 期限那一轮里，引擎读完收件箱之后、期限写锁之前落库的一条扫码（审查 S1 的窗口）：本站结束的那一次改动一并答它
    /// <c>WORKLIST_REVISION_STALE</c>，号是空清单的号，并发上线。修之前它既不装也不拒。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnEntryLandingBetweenTheInboxReadAndTheDeadlineLockIsAnsweredWhenTheStopEnds()
    {
        EntryInterleaver interleaver = new();
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: interleaver);
        await EndTheSecondPickupByItsDeadlineAsync(fixture, interleaver: interleaver);

        // 前提：它确实落在窗口里——触发了一次，而且没被当成这一站的录入装货。
        Assert.Equal(1, interleaver.Fired);
        Assert.False(await AnsweredByALoadCommandAsync(fixture, InterleavedSubmissionId));

        JsonElement rejection = await RejectionOfAsync(fixture, InterleavedSubmissionId);
        Assert.Equal("WORKLIST_REVISION_STALE", rejection.GetProperty("problem").GetProperty("reasonCode").GetString());
        Snapshot empty = Assert.Single(
            await WorklistsAsync(fixture), item => item.Payload.GetProperty("items").GetArrayLength() == 0);
        Assert.Equal(empty.Revision, rejection.GetProperty("currentWorklistRevision").GetInt64());
        string rejectionId = LateSublotSubmission.RejectionMessageId(InterleavedSubmissionId);
        Assert.Contains(fixture.Peer.Lines, bytes => SentAs(Line(bytes), rejectionId, 1));
    }

    /// <summary>
    /// 同一个窗口，但这一站是整趟旅程唯一的一站、期限直接收尾（A 形态）：收尾那一次改动一并答它，号是收尾清单的号。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnEntryLandingInTheWindowIsAnsweredWhenTheDeadlineClosesTheJourney()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        EntryInterleaver interleaver = new();
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(commands: interleaver);
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        JourneyRuntimeRow waiting = await ArriveAtPickupAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, waiting.Stage);
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.HearFromPeerAsync();
        interleaver.Arm(fixture, InterleavedSubmissionId, await SublotSubmissionAsync(fixture, waiting, FirstSublot));
        await TickAndRunAsync(fixture);

        Assert.Equal(1, interleaver.Fired);
        JourneyRuntimeRow closed = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.Equal((JourneyRuntimeStage.Completed, "CANCELLED_BY_STATION_TIMEOUT"), (closed.Stage, closed.BlockReasonCode));
        Assert.Equal(0, await fixture.Context.StationOperations.CountAsync(token));

        JsonElement rejection = await RejectionOfAsync(fixture, InterleavedSubmissionId);
        Assert.Equal("WORKLIST_REVISION_STALE", rejection.GetProperty("problem").GetProperty("reasonCode").GetString());
        IReadOnlyList<Snapshot> closure =
            await AssertClosureStagedAsync(fixture.Context, closed.AgvId, closed.PickupStationId);
        Assert.Equal(
            closure.Single(item => item.MessageType == WorklistType).Revision,
            rejection.GetProperty("currentWorklistRevision").GetInt64());
        string rejectionId = LateSublotSubmission.RejectionMessageId(InterleavedSubmissionId);
        Assert.Contains(fixture.Peer.Lines, bytes => SentAs(Line(bytes), rejectionId, 1));
    }

    /// <summary>
    /// 第二个取货站挂着两条待做项（乙、丙），一次期限一起结束：两次终结只有最后一次暂存空清单，这一站此前发给车的每一版都退役。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task OneDeadlineEndingTwoEntriesAtTheStopSendsOneEmptyWorklistAndRetiresEveryEarlierVersion()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyStopRow secondPickup = await EndTheSecondPickupByItsDeadlineAsync(fixture, thirdAtTheSecondPickup: true);

        // 前提：丙与乙挂在同一个停靠上，两条都被这一次期限终结。
        JourneyDemandRow third = await MembershipAsync(fixture, ThirdDemandId);
        Assert.Equal(secondPickup.StopId, third.PickupStopId);
        Assert.Equal(JourneyDemandStatuses.Terminated, third.Status);

        Snapshot[] atStop = [.. (await WorklistsAsync(fixture))
            .Where(item => item.Payload.GetProperty("stationId").GetString() == secondPickup.StationId)
            .OrderBy(item => item.Revision)];
        Snapshot empty = Assert.Single(atStop, item => item.Payload.GetProperty("items").GetArrayLength() == 0);
        Assert.Equal(atStop[^1].MessageId, empty.MessageId);
        string[] earlier = [.. atStop.Where(item => item.MessageId != empty.MessageId).Select(item => item.MessageId)];
        Assert.NotEmpty(earlier);
        Assert.All(
            await fixture.Context.ProtocolOutbox.AsNoTracking().Where(row => earlier.Contains(row.MessageId)).ToArrayAsync(token),
            row => Assert.True(row.FencedAt is not null || row.AcknowledgedAt is not null, $"{row.MessageId} 还会被补发。"));
    }

    /// <summary>
    /// 受理两条需求（第二条在另一个取货站）、第一条在第一站装上车，车到第二个取货站等录入，期限到期那一轮跑完。
    /// 返回第二个取货停靠。
    /// </summary>
    private static async Task<JourneyStopRow> EndTheSecondPickupByItsDeadlineAsync(
        RuntimeFixture fixture,
        bool secondUnloadsAtItsOwnStop = false,
        bool refillFirst = false,
        bool thirdAtTheSecondPickup = false,
        EntryInterleaver? interleaver = null)
    {
        JourneyStopRow secondPickup = await ArriveAtTheSecondPickupAsync(
            fixture, secondUnloadsAtItsOwnStop, thirdAtTheSecondPickup);

        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        if (refillFirst)
        {
            // cs#339 的形状：断线那一轮作废离站等待，重连回到 Ready 之后从此刻重填，清单升一版带新期限。
            await fixture.DropOnboardSessionAsync();
            fixture.Clock.Advance(TimeSpan.FromSeconds(7));
            await TickAndRunAsync(fixture);
            await ArrivalPublishInterruptedThenReconnectedTests.ReconnectAtGenerationAsync(fixture, 2);
            await fixture.HearFromPeerAsync();
            await TickAndRunAsync(fixture);
            Assert.Equal(1, (await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
                .SingleAsync(row => row.StopId == secondPickup.StopId, TestContext.Current.CancellationToken)).WorklistRefills);
        }
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.HearFromPeerAsync();
        if (interleaver is not null)
        {
            // 录入地址按这一刻的当前停靠算好再武装：拦截器在引擎那一轮里触发，那时不能再用引擎的上下文。
            object entry = await SublotSubmissionAsync(fixture, await JourneyOfAsync(fixture, FirstDemandId), SecondSublot);
            interleaver.Arm(fixture, InterleavedSubmissionId, entry);
        }
        await TickAndRunAsync(fixture);
        Assert.Equal(
            JourneyDemandStatuses.Terminated,
            (await MembershipAsync(fixture, SecondDemandId)).Status);
        return secondPickup;
    }

    /// <summary>
    /// 受理两条需求（第二条在另一个取货站）、第一条在第一站装上车，车到第二个取货站等录入。返回第二个取货停靠。
    /// </summary>
    private static async Task<JourneyStopRow> ArriveAtTheSecondPickupAsync(
        RuntimeFixture fixture, bool secondUnloadsAtItsOwnStop = false, bool thirdAtTheSecondPickup = false)
    {
        fixture.Catalog.Set(
            fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)),
            fixture.Demand(SecondDemandId, SecondSublot, Now.AddMinutes(-9), area: SecondPickupArea),
            fixture.Demand(ThirdDemandId, ThirdSublot, Now.AddMinutes(-8), area: SecondPickupArea));
        fixture.BoxCounts.Set(FirstSublot, 7);
        fixture.BoxCounts.Set(SecondSublot, 7);
        fixture.BoxCounts.Set(ThirdSublot, 7);

        await TickAndRunAsync(fixture);
        await Batch7ThreeStopJourneyTests.AppendSecondDemandAsync(fixture);
        if (thirdAtTheSecondPickup)
        {
            await Batch7ThreeStopJourneyTests.AppendDemandAsync(fixture, ThirdDemandId, ThirdSublot, SecondPickupArea, 13);
        }
        if (secondUnloadsAtItsOwnStop)
        {
            await GiveTheSecondDemandItsOwnUnloadStopAsync(fixture);
        }
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        await ArriveAtCurrentStopAsync(fixture, SecondDemandId, "TO_GATE");
        JourneyStopRow secondPickup = await CurrentStopAsync(fixture, SecondDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        return secondPickup;
    }

    private const string ThirdDemandId = "10000000-0000-4000-8000-000000000003";
    private const string ThirdSublot = "SUBLOT-003";
    private const string InterleavedSubmissionId = "e1000000-0000-4000-8000-000000000001";

    /// <summary>
    /// 在引擎读完收件箱里的录入之后、期限写锁之前，从另一个上下文落一条录入——审查 S1 说的那个窗口。武装之后只触发一次。
    /// </summary>
    /// <remarks>
    /// 认的是那条读收件箱里 <c>SublotSubmitted</c> 的命令的读取器关闭那一刻：读已经完成、引擎还没进期限事务。插入用另一个上下文
    /// （同一条内存连接），不碰引擎正在用的那一个。
    /// </remarks>
    internal sealed class EntryInterleaver(Func<string, bool>? matches = null) : DbCommandInterceptor
    {
        /// <summary>默认：读收件箱里 <c>SublotSubmitted</c> 的那条命令。</summary>
        private readonly Func<string, bool> _matches = matches ?? (text =>
            text.Contains("ProtocolInbox", StringComparison.Ordinal) &&
            text.Contains("SublotSubmitted", StringComparison.Ordinal));

        /// <summary>
        /// 引擎找录入那一段的<b>最后一条</b>读取：按 correlationId 查这条录入有没有被装货命令答复过
        /// （<c>JourneyRuntimeEngine.ConsumedSubmissionIdsAsync</c>）。在它之后触发，引擎已经按锁外读到的「没被拒收、没被消费」认定了录入。
        /// </summary>
        public static bool AfterTheEntryWasJudgedUnanswered(string text) =>
            text.Contains("ProtocolOutbox", StringComparison.Ordinal) &&
            text.Contains("SlotOperationCommand", StringComparison.Ordinal);

        private Func<Task>? _pending;

        public int Fired { get; private set; }

        public void Arm(RuntimeFixture fixture, string messageId, object payload) =>
            _pending = () => InsertInboxAsync(fixture, messageId, payload);

        /// <summary>在那一刻做任意一件事（例如让一条取消结果落定），同样只一次。</summary>
        public void Arm(Func<Task> action) => _pending = action;

        public override async ValueTask<InterceptionResult> DataReaderClosingAsync(
            DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
        {
            if (_pending is { } insert && _matches(command.CommandText))
            {
                _pending = null;
                Fired++;
                await insert();
            }
            return result;
        }
    }

    /// <summary>同 <c>Batch7StopDrivenAdvanceDriver.AddInboxAsync</c>，但走另一个上下文。</summary>
    internal static async Task InsertInboxAsync(RuntimeFixture fixture, string messageId, object payload)
    {
        await using ControlServerDbContext other = fixture.OpenConnectionContext();
        other.ProtocolInbox.Add(new ProtocolInboxRow
        {
            MessageId = messageId,
            MessageType = "SublotSubmitted",
            RequestJson = BeforeSublotEnvelope(fixture, messageId, "SublotSubmitted", generation: 1, payload),
            ContentHash = new string('a', 64),
            FirstResponseJson = "{}",
            ReceivedAt = fixture.Clock.GetUtcNow()
        });
        await other.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static string SecondUnloadStopId(JourneyRuntimeRow runtime) => $"{runtime.JourneyId}|SECOND-UNLOAD";

    /// <summary>
    /// 在关卡站上另挂一个只属于乙的卸货停靠（序位排在最后），乙的归属改指向它。形状照
    /// <c>Batch7ThreeStopJourneyTests.LeavingAnUnloadStopAuthorisesTheLegToTheNextStop</c>：卸货停靠不带录入请求与离站核验的 id。
    /// </summary>
    private static async Task GiveTheSecondDemandItsOwnUnloadStopAsync(RuntimeFixture fixture)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        JourneyRuntimeRow runtime = await JourneyOfAsync(fixture, FirstDemandId);
        string stopId = SecondUnloadStopId(runtime);
        int last = await fixture.Context.Set<JourneyStopRow>()
            .Where(row => row.JourneyId == runtime.JourneyId).MaxAsync(row => row.Sequence, token);
        fixture.Context.Set<JourneyStopRow>().Add(new JourneyStopRow
        {
            StopId = stopId,
            JourneyId = runtime.JourneyId,
            Sequence = last + 1,
            StopRole = JourneyStopRoles.Unload,
            StationId = runtime.GateStationId,
            StationRiotId = runtime.GateStationRiotId,
            DispatchZone = runtime.DispatchZone,
            OperationSessionId = JourneyPlanBuilder.StableGuid(stopId, "session"),
            MovementLegId = JourneyPlanBuilder.StableGuid(stopId, "leg"),
            UpperId = $"W2G-{stopId}",
            VehicleBusinessMessageId = JourneyPlanBuilder.StableGuid(stopId, "vehicle-state"),
            WorklistMessageId = JourneyPlanBuilder.StableGuid(stopId, "worklist"),
            PlanMessageId = JourneyPlanBuilder.StableGuid(stopId, "plan"),
            Status = JourneyStopStatuses.Pending,
            CreatedAt = runtime.CreatedAt
        });
        JourneyDemandRow second = await fixture.Context.Set<JourneyDemandRow>()
            .SingleAsync(row => row.DemandId == SecondDemandId, token);
        second.UnloadStopId = stopId;
        await fixture.Context.SaveChangesAsync(token);
    }

    /// <summary>对这条录入的拒收报文：恰好一条，返回它的载荷。</summary>
    private static async Task<JsonElement> RejectionOfAsync(RuntimeFixture fixture, string submissionId)
    {
        JsonElement[] rejections = await RejectionsOfAsync(fixture, submissionId);
        return Assert.Single(rejections);
    }

    /// <summary>发件箱里 correlationId 是这条录入的全部 SublotRejected 的载荷。</summary>
    private static async Task<JsonElement[]> RejectionsOfAsync(RuntimeFixture fixture, string submissionId)
    {
        string[] rows = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "SublotRejected")
            .Select(row => row.PayloadJson)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        List<JsonElement> matching = [];
        foreach (string json in rows)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.GetProperty("correlationId").GetString() == submissionId)
            {
                matching.Add(document.RootElement.GetProperty("payload").Clone());
            }
        }
        return [.. matching];
    }

    /// <summary>这条录入是否被一条装货命令答复了（命令的 correlationId 就是它）。</summary>
    private static async Task<bool> AnsweredByALoadCommandAsync(RuntimeFixture fixture, string submissionId)
    {
        string[] commands = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "SlotOperationCommand")
            .Select(row => row.PayloadJson)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        return commands.Any(json =>
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.GetProperty("correlationId").GetString() == submissionId;
        });
    }

    private static async Task<Snapshot[]> WorklistsAsync(RuntimeFixture fixture) =>
        [.. (await SnapshotsAsync(fixture.Context, fixture.Options.AgvId)).Where(item => item.MessageType == WorklistType)];

    private static Task<JourneyDemandRow> MembershipAsync(RuntimeFixture fixture, string demandId) =>
        fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == demandId, TestContext.Current.CancellationToken);

    private static string Line(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes);

    /// <summary>握手已完成的连接：真实连接在恢复报告被答复之后恒为如此，答复只在那之后发。</summary>
    private static OnboardConnectionState Connection(RuntimeFixture fixture)
    {
        OnboardConnectionState state =
            JourneyRuntimeWorkerLoadCancellationBeforeSublotTests.BeforeSublotConnection(fixture, generation: 1);
        state.HandshakeCompleted = true;
        return state;
    }

    private static object Operator(RuntimeFixture fixture) => new
    {
        operatorId = "OP-001",
        verificationMethod = "BADGE",
        verifiedAt = fixture.Clock.GetUtcNow()
    };

    private static string Envelope(RuntimeFixture fixture, string messageId, string messageType, object payload) =>
        BeforeSublotEnvelope(fixture, messageId, messageType, generation: 1, payload);

    private static JsonElement FirstLinePayload(string wire)
    {
        string first = wire.Split('\n', StringSplitOptions.RemoveEmptyEntries).First();
        using JsonDocument document = JsonDocument.Parse(first);
        return document.RootElement.GetProperty("payload").Clone();
    }

    private static string FirstLineType(string wire)
    {
        string first = wire.Split('\n', StringSplitOptions.RemoveEmptyEntries).First();
        using JsonDocument document = JsonDocument.Parse(first);
        return document.RootElement.GetProperty("messageType").GetString()!;
    }
}
