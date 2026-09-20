using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 一趟三个停靠的旅程，从受理一路跑到两条需求都卸完（票面第 1 条，批次7-06，control-server#211）。
/// </summary>
/// <remarks>
/// <para>
/// <b>这是本票唯一一条让车真的离开第一个停靠的用例。</b>在它之前，四个 L2 场景与全部单测都停在第一个停靠上
/// ——四份 <c>evidence/l2/*/snapshots/db-JourneyStops.json</c> 里每一个停靠的 <c>Status</c> 都是
/// <c>PENDING</c>，没有任何一个走到过 <c>COMPLETED</c>。追加、插位、九腿八项、账本这些都在第一站之前判完，
/// 所以它们覆盖得再密，也照不到第二站往后的任何一行代码。
/// </para>
/// <para>
/// 它被写成一条<b>构造型</b>的护栏而不是几条针对具体缺陷的用例：一趟多停靠旅程跑不跑得完，是这张票名字本身的
/// 内容，而按症状逐条补的用例修完就没有东西守着下一个同族缺陷了。
/// </para>
/// <para>
/// 两个取货站<b>必须不同</b>（12 与 13）。同站会让「到站判定绑死锚需求那一个站」这一族缺陷全部蒙混过关——
/// <c>multi-stop-append-same-zone</c> 那条 L2 场景就是同站，所以它绿得毫无意义。
/// </para>
/// </remarks>
public sealed class Batch7ThreeStopJourneyTests
{
    /// <summary>第二条需求的 AREA，解析到站 13——与第一条的 N1-1（站 12）不是同一个站。</summary>
    private const string SecondPickupArea = "N1-2";

    private const string ThirdDemandId = "10000000-0000-4000-8000-000000000003";
    private const string ThirdSublot = "SUBLOT-003";

    private const int FirstPickupStationRiotId = 12;
    private const int SecondPickupStationRiotId = 13;

    /// <summary>一维站点图，与 <see cref="Batch7EnRouteAppendPlannerTests"/> 同一把尺：站号之差乘一千毫米。</summary>
    private static readonly Func<int, int, long?> Distance = (from, to) => Math.Abs(from - to) * 1000L;

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AJourneyWithThreeStopsLoadsAtBothPickupsAndUnloadsBothDemands()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await RunThreeStopJourneyAsync(fixture);

        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync(FirstDemandId)).Stage);
        Assert.All(
            await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken),
            stop => Assert.Equal(JourneyStopStatuses.Completed, stop.Status));
    }

    /// <summary>
    /// 一趟多停靠旅程跑完之后，三条快照流各自的号严格递增，而且下一趟的基准高过本趟用掉的最高号
    /// （批次7-06，control-server#211）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>两件事一条用例守，因为它们的失败形状是同一个：车载端拒收然后拆会话。</b>车载端只按消息类型记修订号
    /// （<c>WireToGateSessionClient</c>），号更低是 <c>SNAPSHOT_REVISION_REGRESSION</c>，号相同而内容不同是
    /// <c>SNAPSHOT_REVISION_CONTENT_CONFLICT</c>，两种都当场断开。
    /// </para>
    /// <para>
    /// <b>趟内递增</b>守的是两套算式互相钳制：到站发「基准 + 序位」，途中追加引起的重发发「上一号 + 1」，
    /// 基准不跟着抬高的话，一次追加就让下一次到站算出与重发相同的号，两次追加算出的号比它还低。
    /// </para>
    /// <para>
    /// <b>跨趟的那一半</b>守的是每趟的预留量：预留是照「一趟两个停靠」定的常数，而停靠数没有上界。判据写成
    /// 「计数器 + 预留量 &gt; 本趟最高号」，那正是下一趟受理时会算出来的基准，所以它直接是下一趟的第一条快照
    /// 会不会撞号——不是一个近似。
    /// </para>
    /// <para>
    /// <b>这条用例守得住什么、守不住什么，写在这里免得下一个人高估它。</b>它守的是<b>算术</b>：预留量够不够、
    /// 两套算式钳不钳得住。它<b>守不住</b>「某个发布点没有结清」——那件事不再由它守，而是由
    /// <c>OnboardJourneyPublisher</c> 的快照原语承担：三条流只能从那里发出去，原语要求每条快照交出自己的号，
    /// 发之前先结清。所以再加一个发布点、甚至再加一条快照流，都不需要记得调用什么。
    /// </para>
    /// <para>
    /// 这个分工是换来的：最初结清写在调用点上，而写完第一处我就漏了第二处（卸货停靠那一处），这条用例当场
    /// 抓到了。抓到之后没有停在「有东西会响」——它响的前提是测试场景走到那个发布点，而新加的发布点往往正是
    /// 场景走不到的那一处。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task EverySnapshotStreamStaysMonotonicAndLeavesRoomForTheNextJourney()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await RunThreeStopJourneyAsync(fixture);

        VehicleSnapshotRevisionRow counter = await CounterAsync(fixture);
        foreach ((string messageType, long reserve) in new[]
        {
            ("VehicleBusinessStateSnapshot", WireToGateStore.RevisionsPerJourney),
            ("CurrentStopWorklistSnapshot", WireToGateStore.RevisionsPerJourney),
            ("UpcomingStopPlanSnapshot", WireToGateStore.PlanRevisionsPerJourney)
        })
        {
            long[] published = await PublishedRevisionsAsync(fixture, messageType);
            Assert.NotEmpty(published);
            Assert.Equal(published.Order().Distinct().ToArray(), published);

            long nextJourneyBase = messageType switch
            {
                "VehicleBusinessStateSnapshot" => counter.VehicleBusinessRevision,
                "CurrentStopWorklistSnapshot" => counter.WorklistRevision,
                _ => counter.PlanRevision
            } + reserve;
            Assert.True(
                nextJourneyBase > published[^1],
                $"{messageType}: 下一趟的基准 {nextJourneyBase} 没有越过本趟用掉的最高号 {published[^1]}。");
        }
    }

    /// <summary>受理、追加、跑完三个停靠，两条需求都卸掉。</summary>
    private static async Task RunThreeStopJourneyAsync(RuntimeFixture fixture)
    {
        fixture.Catalog.Set(
            fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)),
            fixture.Demand(SecondDemandId, SecondSublot, Now.AddMinutes(-9), area: SecondPickupArea));
        fixture.BoxCounts.Set(FirstSublot, 7);
        fixture.BoxCounts.Set(SecondSublot, 7);

        // 受理第一条需求，车出发去第一个取货站；随后把第二条追加进这趟旅程。
        await TickAndRunAsync(fixture);
        await AppendSecondDemandAsync(fixture);
        int[] expectedStations =
            [FirstPickupStationRiotId, SecondPickupStationRiotId, TaskTypeStationRuntimeSeed.GateStationRiotId];
        Assert.Equal(expectedStations, await StationsInSequenceAsync(fixture));

        // 第一个取货站：到站、录入、装货落定、答复离站安全。
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);

        // 第二个取货站：同样四步，走的是同一趟旅程的第二个停靠。
        await ArriveAtStopAsync(fixture, SecondPickupStationRiotId);
        // 车停在第二个取货站上，服务端要认这一站的到站并进入等录入。断言停在这一层而不是断言旅程行上的
        // PickupStationRiotId：那个字段在多停靠下已经没有意义，到站判定也不再读它。
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync(FirstDemandId)).Stage);
        await EnterSublotAtStopAsync(fixture, SecondPickupStationRiotId, SecondSublot, SecondSubmissionId);
        await SettleLoadForDemandAsync(fixture, SecondDemandId);
        await AnswerDepartureSafetyAtStopAsync(fixture, SecondPickupStationRiotId, SecondSafetyResultId);

        // 关卡：两条需求各卸一次，一条一条来。服务端一次只发一条卸货命令——一次只开一个仓门——所以第二条的
        // 命令要等第一条的结果落定、再推一轮才会发出来。
        await ArriveAtStopAsync(fixture, TaskTypeStationRuntimeSeed.GateStationRiotId);
        await ApplySafeResultAsync(fixture, FirstDemandId, SlotOperationType.Unload, SlotBusinessState.Empty);
        await TickAndRunAsync(fixture);
        await ApplySafeResultAsync(fixture, SecondDemandId, SlotOperationType.Unload, SlotBusinessState.Empty);
        await TickAndRunAsync(fixture);

    }

    /// <summary>这条流按发出先后排好的修订号。</summary>
    private static async Task<long[]> PublishedRevisionsAsync(RuntimeFixture fixture, string messageType)
    {
        string property = SnapshotRevisionProperty(messageType)!;
        ProtocolOutboxRow[] rows = [.. (await fixture.Context.ProtocolOutbox.AsNoTracking()
                .Where(row => row.MessageType == messageType)
                .ToArrayAsync(TestContext.Current.CancellationToken))
            .OrderBy(row => row.CreatedAt)
            .ThenBy(row => row.MessageId, StringComparer.Ordinal)];
        List<long> revisions = [];
        foreach (ProtocolOutboxRow row in rows)
        {
            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            revisions.Add(document.RootElement.GetProperty("payload").GetProperty(property).GetInt64());
        }

        return [.. revisions];
    }

    /// <summary>
    /// 一个停靠已经完成之后再追加，新的序位不能与它撞号（票面第 3 条，批次7-06，control-server#211）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 序位重排只覆盖<b>未完成</b>的停靠，而重排从 1 开始数——已经完成的那个停靠仍然占着 1。于是一次追加之后，
    /// 同一趟旅程里有两个序位 1。
    /// </para>
    /// <para>
    /// 序位不是内部编号：发给车的那张计划按它排腿（<c>ORDER_LEGS_BY_SEQUENCE</c> 是本票自己的具名向量断言），
    /// 腿的状态也按它与当前停靠的先后判。撞号之后，已经装完离站的第一个取货会被当成还没走到，重新发给车。
    /// </para>
    /// <para>
    /// 这条用例不需要车开到第二个停靠——只要第一个停靠完成时旅程已经有三个停靠，第三条需求一追加就照得出来。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnAppendAfterAStopCompletedDoesNotReuseThatStopsSequence()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(
            fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)),
            fixture.Demand(SecondDemandId, SecondSublot, Now.AddMinutes(-9), area: SecondPickupArea),
            fixture.Demand(ThirdDemandId, ThirdSublot, Now.AddMinutes(-8), area: SecondPickupArea));
        fixture.BoxCounts.Set(FirstSublot, 7);
        fixture.BoxCounts.Set(SecondSublot, 7);
        fixture.BoxCounts.Set(ThirdSublot, 7);

        await TickAndRunAsync(fixture);
        await AppendSecondDemandAsync(fixture);

        // 把第一个取货停靠跑完，它落到 COMPLETED。
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        Assert.Contains(
            await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken),
            stop => stop.Status == JourneyStopStatuses.Completed);

        await AppendDemandAsync(fixture, ThirdDemandId, ThirdSublot, SecondPickupArea, SecondPickupStationRiotId);

        // 红的时候直接把撞号的那个序位报出来，而不是报「4 个停靠只有 3 个不同的数」。
        int[] sequences = await SequencesAsync(fixture);
        Assert.Empty(sequences.GroupBy(sequence => sequence).Where(group => group.Count() > 1).Select(group => group.Key));
    }

    /// <summary>
    /// 在某一个停靠上录入批次，会话与站点都取<b>那个停靠自己的</b>。
    /// </summary>
    /// <remarks>
    /// 共用的 <c>EnterSublotAsync</c> 取的是旅程行上锚需求的 <c>OperationSessionId</c> 与
    /// <c>PickupStationId</c>：一条被追加进来的需求没有自己的旅程行，那两样在它身上都指着别人的站。
    /// </remarks>
    private static async Task EnterSublotAtStopAsync(
        RuntimeFixture fixture,
        int stationRiotId,
        string sublot,
        string submissionId)
    {
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(FirstDemandId);
        // 录入寄到服务端自己算出来的那个地址：会话、站点、清单修订号三样都取当前停靠的。清单修订号尤其不能
        // 用旅程行上那一个——一个停靠可以发不止一版清单，地址是一个区间，而旅程行上只存着这趟旅程的起点。
        StopEntryAddress address = (await JourneyStopCursor.LoadAsync(
                fixture.Context, runtime, TestContext.Current.CancellationToken))
            .EntryAddressOfCurrentStop(runtime.WorklistRevision);
        Assert.Equal(stationRiotId, (await OpenStopAtAsync(fixture, stationRiotId)).StationRiotId);
        await AddInboxAsync(
            fixture,
            submissionId,
            "SublotSubmitted",
            new
            {
                operationSessionId = address.OperationSessionId,
                stationId = address.StationId,
                worklistRevision = address.CurrentWorklistRevision,
                sublot,
                entryMethod = "SCANNER",
                @operator = new
                {
                    operatorId = "OP-001",
                    verificationMethod = "BADGE",
                    verifiedAt = fixture.Clock.GetUtcNow()
                }
            });
        await TickAndRunAsync(fixture);
    }

    /// <summary>
    /// 在某一个停靠上答复离站安全，核验 id 与它的请求消息都取<b>那个停靠自己的</b>。
    /// </summary>
    /// <remarks>
    /// 共用的 <c>AnswerDepartureSafetyAsync</c> 取旅程行上的 <c>PreDepartureSafetyCheckId</c>，那是第一个停靠
    /// 那一版的同源副本；<c>DepartureCheckId</c>（<c>JourneyRuntimeEngine.cs:2486</c>）读的却是停靠行。
    /// 第二个停靠用旅程行上那一个，答复对不上，旅程停在 <c>AwaitingDepartureSafety</c> 不再前进。
    /// </remarks>
    private static async Task AnswerDepartureSafetyAtStopAsync(
        RuntimeFixture fixture, int stationRiotId, string safetyResultId)
    {
        JourneyStopRow stop = await OpenStopAtAsync(fixture, stationRiotId);
        await AddInboxAsync(
            fixture,
            safetyResultId,
            "PreDepartureSafetyCheckResult",
            SafeDepartureAnswer(fixture, stop.DepartureSafetyCheckId!, 7),
            stop.DepartureSafetyCheckMessageId);
        await TickAndRunAsync(fixture);
    }

    /// <summary>这条需求的装货结果落定，再推一轮。</summary>
    private static async Task SettleLoadForDemandAsync(RuntimeFixture fixture, string demandId)
    {
        await ApplySafeResultAsync(fixture, demandId, SlotOperationType.Load, SlotBusinessState.Occupied);
        await TickAndRunAsync(fixture);
    }

    /// <summary>这个站上还没完成的那个停靠。</summary>
    private static Task<JourneyStopRow> OpenStopAtAsync(RuntimeFixture fixture, int stationRiotId) =>
        fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(
                row => row.StationRiotId == stationRiotId && row.Status != JourneyStopStatuses.Completed,
                TestContext.Current.CancellationToken);

    /// <summary>车开到某一个停靠所在的站，用那个停靠自己的上位机单号。</summary>
    /// <remarks>
    /// 不能用 <c>ArriveAtPickupAsync</c>：那一条取的是旅程行上锚需求的 <c>PickupUpperId</c> 与
    /// <c>PickupStationRiotId</c>，第二个取货站在旅程行上根本没有位置。
    /// </remarks>
    private static async Task ArriveAtStopAsync(RuntimeFixture fixture, int stationRiotId)
    {
        JourneyStopRow stop = await OpenStopAtAsync(fixture, stationRiotId);
        fixture.Riot.SetSuccessfulArrival("TO_GATE", stop.UpperId, stationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = stationRiotId };
        await TickAndRunAsync(fixture);
    }

    /// <summary>全部停靠的序位，未完成与已完成都算在内。</summary>
    private static async Task<int[]> SequencesAsync(RuntimeFixture fixture) =>
        [.. (await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken))
            .Select(row => row.Sequence)];

    /// <summary>按序位排好的停靠站号，未完成与已完成都算在内。</summary>
    private static async Task<int[]> StationsInSequenceAsync(RuntimeFixture fixture) =>
        [.. (await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken))
            .OrderBy(row => row.Sequence)
            .Select(row => row.StationRiotId)];

    /// <summary>
    /// 把第二条需求追加进这趟旅程，插入位由 <see cref="EnRouteAppendPlanner"/> 真算，落库走
    /// <see cref="WireToGateStore.AppendToJourneyAsync"/>——与轮次走的是同一条落库路径。
    /// </summary>
    private static Task AppendSecondDemandAsync(RuntimeFixture fixture) =>
        AppendDemandAsync(fixture, SecondDemandId, SecondSublot, SecondPickupArea, SecondPickupStationRiotId);

    private static async Task AppendDemandAsync(
        RuntimeFixture fixture, string demandId, string sublot, string area, int pickupStationRiotId)
    {
        // 第二个取货站也要在站点准入白名单里。生产里派车链的 StationTaskTypeAdmissionCriterion（Order 90）
        // 查的就是这张表、这个站，所以一条派得出去的需求必然已经在表里；夹具只种了第一个站，因为在这之前
        // 一趟旅程只有一个取货站。补这一行不是绕开那道门，是补上「这条需求本来就能通过它」这个前提——
        // 否则这里构造出的是一条生产里根本不会存在的旅程。
        if (!await fixture.Context.StationTaskTypeAdmissions.AnyAsync(
                row => row.StationId == area && row.TaskType == "WIRE_TO_GATE",
                TestContext.Current.CancellationToken))
        {
            fixture.Context.StationTaskTypeAdmissions.Add(new StationTaskTypeAdmissionRow
            {
                StationId = area,
                TaskType = "WIRE_TO_GATE",
                PolicyVersion = 1
            });
            await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            fixture.Context.ChangeTracker.Clear();
        }

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(FirstDemandId);
        // 整条旅程的停靠都交给规划器，已完成的也在内——与 DispatchRoundRunner.ReadEnRoutePlanAsync 同一口径。
        JourneyStopRow[] stops = [.. (await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
                .Where(row => row.JourneyId == runtime.JourneyId)
                .ToArrayAsync(TestContext.Current.CancellationToken))
            .OrderBy(row => row.Sequence)];
        int currentNextStopIndex = stops.ToList().FindIndex(
            row => row.Status != JourneyStopStatuses.Completed && row.Status != JourneyStopStatuses.Removed);
        string zone = runtime.DispatchZone;
        EnRouteStop pickup = new(
            JourneyIdentity.AppendedPickupStopId(demandId),
            area,
            pickupStationRiotId,
            zone,
            JourneyStopRoles.Pickup);
        EnRouteStop unload = new(
            JourneyIdentity.AppendedUnloadStopId(demandId),
            stops[^1].StationId,
            TaskTypeStationRuntimeSeed.GateStationRiotId,
            zone,
            JourneyStopRoles.Unload);
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            new EnRouteVehiclePlan(
                [.. stops.Select(row => new EnRouteStop(
                    row.StopId, row.StationId, row.StationRiotId, row.DispatchZone, row.StopRole))],
                fixture.Riot.Vehicle.CurrentStationId ?? 0,
                currentNextStopIndex,
                stops.ToDictionary(row => row.StopId, _ => 1, StringComparer.Ordinal)),
            new EnRouteAppendCandidate(pickup, unload, zone),
            Zones(zone, 10_000_000),
            Distance);
        EnRouteAppendPlacement placement = decision.Placement
            ?? throw new InvalidOperationException($"追加被拒：{decision.RefusalReasonCode}");

        JourneyExecutionPlan plan = new(
            runtime.AgvId,
            runtime.VehicleKey,
            runtime.AgvLifecycleGeneration,
            runtime.MapId,
            runtime.MapIdentity,
            zone,
            $"route-{demandId}",
            area,
            pickupStationRiotId,
            stops[^1].StationId,
            TaskTypeStationRuntimeSeed.GateStationRiotId,
            2,
            [3, 4],
            JourneyPlanBuilder.StableGuid(demandId, "operation-session"),
            JourneyPlanBuilder.StableGuid(demandId, "pickup-leg"),
            $"W2G-{demandId}-PICKUP-1",
            JourneyPlanBuilder.StableGuid(demandId, "gate-leg"),
            $"W2G-{demandId}-GATE-1",
            1,
            fixture.Clock.GetUtcNow());
        JourneyAppendPlan append = new(
            runtime.JourneyId,
            demandId,
            plan,
            placement.MergeIntoPickupStopId ?? JourneyIdentity.AppendedPickupStopId(demandId),
            placement.MergeIntoUnloadStopId ?? JourneyIdentity.AppendedUnloadStopId(demandId),
            zone,
            DispatchZoneParameterVersion: 7,
            [.. placement.Resequenced.Select(stop => new JourneyStopSequenceChange(stop.StopId, stop.Sequence))],
            fixture.Clock.GetUtcNow());

        await new WireToGateStore(fixture.Context).AppendToJourneyAsync(
            fixture.Demand(demandId, sublot, Now.AddMinutes(-9), area: area),
            append,
            TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();
    }

    private static DispatchZoneParameterTableVersion Zones(string zone, long allowance) =>
        new(
            Version: 7,
            ContentSha256: new string('0', 64),
            SnapshotId: null,
            LoadedAt: DateTimeOffset.UnixEpoch,
            Source: "test",
            new Dictionary<string, DispatchZoneParameters>(StringComparer.Ordinal)
            {
                [zone] = new DispatchZoneParameters(zone, allowance, null)
            });
}
