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
    /// 这一条守的正是票面那句「同号不同内容会当场断会话」：空清单若按算式发在「做完最后一条」那个号上，那个号就是卸货站的首号，
    /// 这里会看到两版同号。
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
        await processor.FlushDeferredOutboundAsync(Connection(fixture), token);

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
    private static async Task<JourneyStopRow> EndTheSecondPickupByItsDeadlineAsync(RuntimeFixture fixture)
    {
        fixture.Catalog.Set(
            fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)),
            fixture.Demand(SecondDemandId, SecondSublot, Now.AddMinutes(-9), area: SecondPickupArea));
        fixture.BoxCounts.Set(FirstSublot, 7);
        fixture.BoxCounts.Set(SecondSublot, 7);

        await TickAndRunAsync(fixture);
        await Batch7ThreeStopJourneyTests.AppendSecondDemandAsync(fixture);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        await ArriveAtCurrentStopAsync(fixture, SecondDemandId, "TO_GATE");
        JourneyStopRow secondPickup = await CurrentStopAsync(fixture, SecondDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await TickAndRunAsync(fixture);
        Assert.Equal(
            JourneyDemandStatuses.Terminated,
            (await MembershipAsync(fixture, SecondDemandId)).Status);
        return secondPickup;
    }

    private static async Task<Snapshot[]> WorklistsAsync(RuntimeFixture fixture) =>
        [.. (await SnapshotsAsync(fixture.Context, fixture.Options.AgvId)).Where(item => item.MessageType == WorklistType)];

    private static Task<JourneyDemandRow> MembershipAsync(RuntimeFixture fixture, string demandId) =>
        fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == demandId, TestContext.Current.CancellationToken);

    private static string Line(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes);

    private static OnboardConnectionState Connection(RuntimeFixture fixture) =>
        JourneyRuntimeWorkerLoadCancellationBeforeSublotTests.BeforeSublotConnection(fixture, generation: 1);

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
