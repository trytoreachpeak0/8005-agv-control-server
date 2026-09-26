using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
    /// 受理两条需求（第二条在另一个取货站）、第一条在第一站装上车，车到第二个取货站等录入，期限到期那一轮跑完。
    /// 返回第二个取货停靠。
    /// </summary>
    private static async Task<JourneyStopRow> EndTheSecondPickupByItsDeadlineAsync(
        RuntimeFixture fixture, bool secondUnloadsAtItsOwnStop = false, bool refillFirst = false)
    {
        fixture.Catalog.Set(
            fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)),
            fixture.Demand(SecondDemandId, SecondSublot, Now.AddMinutes(-9), area: SecondPickupArea));
        fixture.BoxCounts.Set(FirstSublot, 7);
        fixture.BoxCounts.Set(SecondSublot, 7);

        await TickAndRunAsync(fixture);
        await Batch7ThreeStopJourneyTests.AppendSecondDemandAsync(fixture);
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
        await TickAndRunAsync(fixture);
        Assert.Equal(
            JourneyDemandStatuses.Terminated,
            (await MembershipAsync(fixture, SecondDemandId)).Status);
        return secondPickup;
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
