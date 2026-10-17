using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using ControlServer.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 已被更高一版盖过的旅程快照（业务状态、清单、计划）退役（control-server#750）。
/// </summary>
/// <remarks>
/// <para>
/// <b>修之前。</b>车载端按消息类型记采纳过的最高号，号更低的当回退拒收（回 <c>ProtocolProblem</c>、不回 ack、拆会话），记录断线不丢。断线漏了确认的中途快照于是永久挂在发件箱里：
/// 在途时被同流更高的一版盖过，或旅程收尾之后不再在任何补发集合里；换协议身份时 <c>ProtocolOutboxIdentityStartupCheck</c> 拒绝启动。
/// </para>
/// <para>
/// <b>不认来路。</b>遗留行是哪条路径漏下的不重要：这里直接在发件箱里放「号低于车确认过的最大号」或「号低于已收尾旅程收尾快照」的未确认行，
/// 断言启动修复（以及收尾时的规则 A）之后它们不再待补发；没被盖过的行一行不动。
/// </para>
/// </remarks>
public sealed class SupersededSnapshotOutboxFenceTests
{
    private const string V3Manifest = "4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7";
    private const string AgvA = "agv-750-a";
    private const string AgvB = "agv-750-b";

    private static readonly (string Type, string Revision)[] Streams =
    [
        ("VehicleBusinessStateSnapshot", "vehicleBusinessStateRevision"),
        ("CurrentStopWorklistSnapshot", "worklistRevision"),
        ("UpcomingStopPlanSnapshot", "planRevision")
    ];

    private static readonly string[] SnapshotTypes = [.. Streams.Select(stream => stream.Type)];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 规则 A，三条流各一份：车确认过第 10 版，第 9 版（上一协议身份）还未确认——启动修复退掉它，换身份的启动检查随后放行；
    /// 未确认的最新版（第 11 版）不动。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheStartupRepairFencesLinesBelowTheAcknowledgedRevisionOfEachStream()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        foreach ((string type, string _) in Streams)
        {
            fixture.Context.ProtocolOutbox.Add(Line(fixture, Id(type, 10), AgvA, type, 10, current: true, acknowledged: true));
            fixture.Context.ProtocolOutbox.Add(Line(fixture, Id(type, 9), AgvA, type, 9, current: false));
            fixture.Context.ProtocolOutbox.Add(Line(fixture, Id(type, 11), AgvA, type, 11, current: true));
        }
        await SaveAndClearAsync(fixture);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProtocolOutboxIdentityStartupCheck.EnsureAsync(fixture.Context, NullLogger.Instance, null, Token));

        await using ServiceProvider services = HostServices(fixture);
        await SupersededSnapshotOutbox.FenceSupersededAtStartupAsync(services, Token);

        await ProtocolOutboxIdentityStartupCheck.EnsureAsync(services, Token);
        Assert.Equal(
            SnapshotTypes.Select(type => Id(type, 11)).Order(StringComparer.Ordinal),
            (await PendingSnapshotIdsAsync(fixture)).Order(StringComparer.Ordinal));
        foreach ((string type, string _) in Streams)
        {
            Assert.NotNull((await RowAsync(fixture, Id(type, 9))).FencedAt);
            ProtocolOutboxRow acknowledged = await RowAsync(fixture, Id(type, 10));
            Assert.Null(acknowledged.FencedAt);
            Assert.NotNull(acknowledged.AcknowledgedAt);
        }
    }

    /// <summary>
    /// 幂等：第二次启动修复一行不动，第一次退役的时刻不被改写，身份检查照旧放行。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheStartupRepairIsIdempotent()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        foreach ((string type, string _) in Streams)
        {
            fixture.Context.ProtocolOutbox.Add(Line(fixture, Id(type, 10), AgvA, type, 10, current: true, acknowledged: true));
            fixture.Context.ProtocolOutbox.Add(Line(fixture, Id(type, 9), AgvA, type, 9, current: false));
        }
        await SaveAndClearAsync(fixture);
        await using ServiceProvider services = HostServices(fixture);
        await SupersededSnapshotOutbox.FenceSupersededAtStartupAsync(services, Token);
        DateTimeOffset?[] first = await FencedAtAsync(fixture);

        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            Assert.Equal(0, await SupersededSnapshotOutbox.FenceSupersededAsync(
                scope.ServiceProvider.GetRequiredService<ControlServerDbContext>(), agvId: null, fixture.Clock.GetUtcNow(), Token));
        }
        await SupersededSnapshotOutbox.FenceSupersededAtStartupAsync(services, Token);

        Assert.Equal(first, await FencedAtAsync(fixture));
        await ProtocolOutboxIdentityStartupCheck.EnsureAsync(services, Token);
    }

    /// <summary>
    /// 流按车分：另一辆车确认过的高号不退这辆车的行。这辆车本流没有确认过的行，它的遗留行（上一协议身份）原样留着，启动检查照旧拒绝——不放宽。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnotherVehiclesAcknowledgedRevisionDoesNotFenceThisVehiclesLines()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        foreach ((string type, string _) in Streams)
        {
            fixture.Context.ProtocolOutbox.Add(Line(fixture, Id(type, 500), AgvB, type, 500, current: true, acknowledged: true));
            fixture.Context.ProtocolOutbox.Add(Line(fixture, Id(type, 5), AgvA, type, 5, current: false));
        }
        await SaveAndClearAsync(fixture);

        await using ServiceProvider services = HostServices(fixture);
        await SupersededSnapshotOutbox.FenceSupersededAtStartupAsync(services, Token);

        Assert.Equal(
            SnapshotTypes.Select(type => Id(type, 5)).Order(StringComparer.Ordinal),
            (await PendingSnapshotIdsAsync(fixture)).Order(StringComparer.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProtocolOutboxIdentityStartupCheck.EnsureAsync(services, Token));
    }

    /// <summary>
    /// 流按类型分：业务状态确认过高号，不退同一辆车的清单与计划。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnAcknowledgedRevisionOfOneTypeDoesNotFenceAnotherType()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Context.ProtocolOutbox.Add(
            Line(fixture, Id("VehicleBusinessStateSnapshot", 500), AgvA, "VehicleBusinessStateSnapshot", 500, current: true, acknowledged: true));
        fixture.Context.ProtocolOutbox.Add(
            Line(fixture, Id("CurrentStopWorklistSnapshot", 7), AgvA, "CurrentStopWorklistSnapshot", 7, current: true));
        fixture.Context.ProtocolOutbox.Add(
            Line(fixture, Id("UpcomingStopPlanSnapshot", 7), AgvA, "UpcomingStopPlanSnapshot", 7, current: true));
        await SaveAndClearAsync(fixture);

        await using ServiceProvider services = HostServices(fixture);
        await SupersededSnapshotOutbox.FenceSupersededAtStartupAsync(services, Token);

        Assert.Equal(
            new[] { Id("CurrentStopWorklistSnapshot", 7), Id("UpcomingStopPlanSnapshot", 7) }.Order(StringComparer.Ordinal),
            (await PendingSnapshotIdsAsync(fixture)).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// 规则 B：旅程走完、夹具里没有车来确认，于是它每一版中途快照都还未确认。启动修复之后，这辆车三条流上待补发的恰好是落了库的收尾快照，
    /// 中途版（含一行上一协议身份的遗留）全部退役，换身份的启动检查放行。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheStartupRepairLeavesOnlyTheClosureSnapshotsOfACompletedJourneyPending()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow runtime = await CompleteOneJourneyAsync(fixture);
        // 修之前写下的样子：比收尾快照先写下、号更低、上一协议身份。
        foreach ((string type, string _) in Streams)
        {
            fixture.Context.ProtocolOutbox.Add(Line(
                fixture, Id(type, 0), fixture.Options.AgvId, type, 0, current: false, createdAt: fixture.Clock.GetUtcNow().AddHours(-1)));
        }
        await SaveAndClearAsync(fixture);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProtocolOutboxIdentityStartupCheck.EnsureAsync(fixture.Context, NullLogger.Instance, null, Token));
        string[] closures = await StoredClosureIdsAsync(fixture, runtime.JourneyId);
        Assert.NotEmpty(closures);
        Assert.True(
            (await PendingSnapshotIdsAsync(fixture)).Length > closures.Length + Streams.Length,
            "the journey should have left mid-journey snapshots pending, or this test proves nothing");

        await using ServiceProvider services = HostServices(fixture);
        await SupersededSnapshotOutbox.FenceSupersededAtStartupAsync(services, Token);

        Assert.Equal(closures.Order(StringComparer.Ordinal), (await PendingSnapshotIdsAsync(fixture)).Order(StringComparer.Ordinal));
        await ProtocolOutboxIdentityStartupCheck.EnsureAsync(services, Token);
        // 幂等：再跑一次不动任何行。
        DateTimeOffset?[] first = await FencedAtAsync(fixture);
        await SupersededSnapshotOutbox.FenceSupersededAtStartupAsync(services, Token);
        Assert.Equal(first, await FencedAtAsync(fixture));
    }

    /// <summary>
    /// 规则 B 只退比收尾快照先写下的行：收尾之后才写下的低号行（不是这一趟的中途版）不替它判。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheClosureRuleLeavesLinesWrittenAfterTheClosureAlone()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await CompleteOneJourneyAsync(fixture);
        foreach ((string type, string _) in Streams)
        {
            fixture.Context.ProtocolOutbox.Add(Line(
                fixture, Id(type, 0), fixture.Options.AgvId, type, 0, current: true, createdAt: fixture.Clock.GetUtcNow().AddHours(1)));
        }
        await SaveAndClearAsync(fixture);

        await using ServiceProvider services = HostServices(fixture);
        await SupersededSnapshotOutbox.FenceSupersededAtStartupAsync(services, Token);

        foreach ((string type, string _) in Streams)
        {
            Assert.Null((await RowAsync(fixture, Id(type, 0))).FencedAt);
        }
    }

    /// <summary>
    /// 在途旅程（还在取货站上）：车一个都没确认时启动修复一行不退；车确认了每条流上除最新一版之外的全部之后，再跑一次仍一行不退，
    /// 未确认的最新版照旧待补发。
    /// </summary>
    /// <remarks>
    /// 也是车载端日志簿被交还（换库、重部署）之后的样子：新库里的号从头开始、没有旧的已确认行，规则 A 没有门槛可比；
    /// 在途旅程没有收尾快照，规则 B 也碰不到它（C11 10-17 要求钉住）。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheLatestUnacknowledgedVersionsOfAJourneyInFlightAreNeverFenced()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        Assert.NotEqual(JourneyRuntimeStage.Completed, (await ArriveAtPickupAsync(fixture, FirstDemandId)).Stage);
        fixture.Context.ChangeTracker.Clear();
        string[] pendingBefore = await PendingSnapshotIdsAsync(fixture);
        Assert.NotEmpty(pendingBefore);

        DateTimeOffset?[] fencedAtArrival = await FencedAtAsync(fixture);
        await using ServiceProvider services = HostServices(fixture);
        await SupersededSnapshotOutbox.FenceSupersededAtStartupAsync(services, Token);
        Assert.Equal(fencedAtArrival, await FencedAtAsync(fixture));
        Assert.Equal(pendingBefore.Order(StringComparer.Ordinal), (await PendingSnapshotIdsAsync(fixture)).Order(StringComparer.Ordinal));

        // 车确认每条流上除最新一版以外的全部（按号；同号取任一）。
        string[] latest = await AcknowledgeAllButLatestAsync(fixture);
        // 引擎自己退过的行（到站时退掉路上那版计划）不算：断言的是启动修复前后退役状态一行不变。
        DateTimeOffset?[] fencedBefore = await FencedAtAsync(fixture);
        await SupersededSnapshotOutbox.FenceSupersededAtStartupAsync(services, Token);

        Assert.Equal(latest.Order(StringComparer.Ordinal), (await PendingSnapshotIdsAsync(fixture)).Order(StringComparer.Ordinal));
        Assert.Equal(fencedBefore, await FencedAtAsync(fixture));
    }

    /// <summary>
    /// 车载端日志簿被交还（换库、重部署）之后：新库里的号从头开始（C11 10-17 要求）。新库先真跑完一趟旅程，下一趟每条流在收尾快照之上发两版、
    /// 车确认了前一版：启动修复只退被确认盖过的旧行，下一趟的两版一行不退，未确认的最新版照旧待补发。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 规则 A：新库没有旧库那些高号的已确认行，门槛只来自本库自己的确认。规则 B：下一趟的号在第一趟收尾快照之上、写入也在它之后，两个条件都碰不到。
    /// 第一趟的收尾快照被下一趟确认过的更高一版盖过，由 A 退掉，那是对的：车已经采纳了更高的。
    /// </para>
    /// <para>
    /// 下一趟的行直接写进发件箱：夹具里同车第二趟受理后停在 <c>AwaitingPickupArrival</c>、不发快照（<c>Batch7VehicleOccupancyReleaseTests</c> 同样只走到那里）。
    /// 号取「第一趟本流最大号 + 1、+ 2」，即下一趟基准的下限（受理时基准 = 按车计数器 + 预留，计数器已被收尾快照抬到收尾号）。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AfterTheOnboardJournalIsHandedBackRevisionsRestartAndTheNextJourneyIsNotFenced()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow first = await CompleteOneJourneyAsync(fixture);
        ProtocolOutboxRow[] firstJourney = await AllSnapshotRowsAsync(fixture);
        Assert.All(firstJourney, row => Assert.True(
            Revision(row) < 1000, $"a fresh database should number from the start, {row.MessageType} has {Revision(row)}"));
        DateTimeOffset later = fixture.Clock.GetUtcNow().AddMinutes(10);
        List<string> nextAcknowledged = [];
        List<string> nextLatest = [];
        foreach ((string type, string _) in Streams)
        {
            long top = firstJourney.Where(row => row.MessageType == type).Select(Revision).DefaultIfEmpty(0).Max();
            ProtocolOutboxRow acknowledged = Line(
                fixture, Id(type, top + 1), fixture.Options.AgvId, type, top + 1, current: true, acknowledged: true, createdAt: later);
            ProtocolOutboxRow latest = Line(
                fixture, Id(type, top + 2), fixture.Options.AgvId, type, top + 2, current: true, createdAt: later.AddSeconds(1));
            fixture.Context.ProtocolOutbox.AddRange(acknowledged, latest);
            nextAcknowledged.Add(acknowledged.MessageId);
            nextLatest.Add(latest.MessageId);
        }
        await SaveAndClearAsync(fixture);

        await using ServiceProvider services = HostServices(fixture);
        await SupersededSnapshotOutbox.FenceSupersededAtStartupAsync(services, Token);

        // 待补发的恰好是下一趟未确认的最新版：第一趟的中途版与收尾快照都已被盖过。
        Assert.Equal(nextLatest.Order(StringComparer.Ordinal), (await PendingSnapshotIdsAsync(fixture)).Order(StringComparer.Ordinal));
        foreach (string id in nextAcknowledged.Concat(nextLatest))
        {
            Assert.Null((await RowAsync(fixture, id)).FencedAt);
        }
        Assert.All(
            await StoredClosureIdsAsync(fixture, first.JourneyId),
            id => Assert.Contains(id, firstJourney.Select(row => row.MessageId)));
    }

    /// <summary>
    /// 运行时规则 A：旅程收尾时，这辆车上已被确认过的更高一版盖过的未确认行当场退役，不等下次启动。
    /// 规则 B 收尾时不做：同一次收尾里没被确认盖过的中途版此刻仍待补发（但已不在补发集合里），留给启动修复。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ClosingAJourneyFencesLinesBelowTheAcknowledgedRevisionAtOnce()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        const string business = "VehicleBusinessStateSnapshot";
        fixture.Context.ProtocolOutbox.Add(Line(fixture, Id(business, 900), fixture.Options.AgvId, business, 900, current: true, acknowledged: true));
        fixture.Context.ProtocolOutbox.Add(Line(fixture, Id(business, 899), fixture.Options.AgvId, business, 899, current: true));
        await SaveAndClearAsync(fixture);
        Assert.Null((await RowAsync(fixture, Id(business, 899))).FencedAt);
        string[] worklistsBefore = await PendingIdsOfAsync(fixture, "CurrentStopWorklistSnapshot");
        Assert.NotEmpty(worklistsBefore);

        Assert.Equal(JourneyRuntimeStage.Completed, (await ArriveAtGateAndUnloadAsync(fixture, FirstDemandId)).Stage);
        fixture.Context.ChangeTracker.Clear();

        Assert.NotNull((await RowAsync(fixture, Id(business, 899))).FencedAt);
        // 这一趟的中途清单没有被确认盖过：收尾时不退。
        string[] worklistsAfter = await PendingIdsOfAsync(fixture, "CurrentStopWorklistSnapshot");
        Assert.All(worklistsBefore, id => Assert.Contains(id, worklistsAfter));
    }

    /// <summary>
    /// 运行时规则 A 在充电收尾（<c>JourneyClosure.StageChargingAsync</c>）里同样当场退（审查 S3）：这辆车上被确认过的更高一版盖过的行退役，
    /// 别的车的行不动。
    /// </summary>
    /// <remarks>
    /// 直接调收尾入口，不搭一整趟充电：规则 A 在读会话之前，夹具里没有这辆车的会话行，入口退役之后就返回、不暂存收尾快照。
    /// 另外三个非搬运收尾入口（空闲返回、等待点清桩、充满）同一行写法，没有各自的用例，见 PR 正文。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ClosingAChargingJourneyFencesLinesBelowTheAcknowledgedRevisionAtOnce()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        const string business = "VehicleBusinessStateSnapshot";
        fixture.Context.ProtocolOutbox.Add(Line(fixture, Id(business, 900), AgvA, business, 900, current: true, acknowledged: true));
        fixture.Context.ProtocolOutbox.Add(Line(fixture, Id(business, 899), AgvA, business, 899, current: true));
        fixture.Context.ProtocolOutbox.Add(Line(fixture, Id(business, 800), AgvB, business, 800, current: true));
        await SaveAndClearAsync(fixture);
        JourneyRuntimeRow charging = ChargingJourneyShape.Build(
            ChargingIdentity.JourneyIdFor("V750A", fixture.Clock.GetUtcNow()),
            new FleetVehicle(AgvA, "V750A", 1),
            new ChargerRosterEntry(fixture.Options.MapId, 750, "CHG-750", null, null, []),
            1,
            "MANDATORY_CHARGE",
            fixture.Options,
            fixture.Clock.GetUtcNow()).Runtime;
        Assert.Equal(AgvA, charging.AgvId);

        await JourneyClosure.StageChargingAsync(fixture.Context, charging, "CHARGING_TEST_END", fixture.Clock.GetUtcNow(), Token);
        await SaveAndClearAsync(fixture);

        Assert.Equal(JourneyRuntimeStage.Completed, charging.Stage);
        Assert.NotNull((await RowAsync(fixture, Id(business, 899))).FencedAt);
        Assert.Null((await RowAsync(fixture, Id(business, 900))).FencedAt);
        Assert.Null((await RowAsync(fixture, Id(business, 800))).FencedAt);
    }

    /// <summary>
    /// 主机的启动顺序：库迁移之后、发件箱身份检查之前跑这一项启动修复（同 <c>StopScopedOutboxFenceTests</c> 那一条：直接调入口的测试看不见 <c>Program.cs</c>）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public void TheHostFencesSupersededSnapshotsAfterMigratingAndBeforeTheOutboxIdentityCheck()
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), "src", "ControlServer.Host", "Program.cs"));
        int Line(string call)
        {
            int[] found = [.. lines.Select((line, index) => (line.Trim(), index))
                .Where(item => !item.Item1.StartsWith("//", StringComparison.Ordinal)
                               && item.Item1.StartsWith("await " + call + "(app.Services", StringComparison.Ordinal))
                .Select(item => item.index)];
            return Assert.Single(found);
        }

        int migrate = Line("EnsureDatabaseAsync");
        int repair = Line("SupersededSnapshotOutbox.FenceSupersededAtStartupAsync");
        int identity = Line("ProtocolOutboxIdentityStartupCheck.EnsureAsync");

        Assert.True(migrate < repair, $"the repair at line {repair + 1} runs before the migration at line {migrate + 1}");
        Assert.True(repair < identity, $"the repair at line {repair + 1} runs after the identity check at line {identity + 1}");
    }

    /// <summary>一个需求的旅程从受理走到卸完（收尾），夹具里没有车来确认任何快照。</summary>
    private static async Task<JourneyRuntimeRow> CompleteOneJourneyAsync(RuntimeFixture fixture)
    {
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        JourneyRuntimeRow runtime = await ArriveAtGateAndUnloadAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        fixture.Context.ChangeTracker.Clear();
        return runtime;
    }

    private static async Task<string[]> StoredClosureIdsAsync(RuntimeFixture fixture, string journeyId)
    {
        string[] ids = [.. JourneyClosure.SnapshotMessageIds(journeyId)];
        return await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => ids.Contains(row.MessageId))
            .Select(row => row.MessageId)
            .ToArrayAsync(Token);
    }

    private static async Task<string[]> AcknowledgeAllButLatestAsync(RuntimeFixture fixture)
    {
        ProtocolOutboxRow[] rows = await fixture.Context.ProtocolOutbox
            .Where(row => SnapshotTypes.Contains(row.MessageType) && row.AcknowledgedAt == null && row.FencedAt == null)
            .ToArrayAsync(Token);
        List<string> latest = [];
        foreach (IGrouping<string, ProtocolOutboxRow> stream in rows.GroupBy(row => row.MessageType))
        {
            ProtocolOutboxRow[] ordered = [.. stream.OrderBy(row => Revision(row))];
            latest.Add(ordered[^1].MessageId);
            foreach (ProtocolOutboxRow row in ordered[..^1])
            {
                row.AcknowledgedAt = fixture.Clock.GetUtcNow();
            }
        }
        await SaveAndClearAsync(fixture);
        return [.. latest];
    }

    private static long Revision(ProtocolOutboxRow row)
    {
        using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
        string property = Streams.Single(stream => stream.Type == row.MessageType).Revision;
        return document.RootElement.GetProperty("payload").GetProperty(property).GetInt64();
    }

    private static ProtocolOutboxRow Line(
        RuntimeFixture fixture,
        string messageId,
        string agvId,
        string messageType,
        long revision,
        bool current,
        bool acknowledged = false,
        DateTimeOffset? createdAt = null)
    {
        string property = Streams.Single(stream => stream.Type == messageType).Revision;
        Dictionary<string, object?> payload = new() { [property] = revision };
        return new ProtocolOutboxRow
        {
            MessageId = messageId,
            MessageType = messageType,
            PayloadJson = JsonSerializer.Serialize(new
            {
                protocolVersion = current ? ProtocolCandidateIdentity.ProtocolVersion : 3,
                profileId = ProtocolCandidateIdentity.ProfileId,
                protocolReleaseVersion = current ? ProtocolCandidateIdentity.ReleaseVersion : "2.0.0",
                protocolReleaseManifestSha256 = current ? ProtocolCandidateIdentity.ManifestSha256 : V3Manifest,
                messageType,
                messageId,
                correlationId = (string?)null,
                agvId,
                sessionGeneration = 1,
                sentAt = "2026-10-15T07:13:00Z",
                payload
            }),
            CreatedAt = createdAt ?? fixture.Clock.GetUtcNow(),
            AcknowledgedAt = acknowledged ? fixture.Clock.GetUtcNow() : null
        };
    }

    /// <summary>每条流、每个号一个固定 id，前缀标出票号。</summary>
    private static string Id(string messageType, long revision) =>
        $"c7500000-{Array.IndexOf(SnapshotTypes, messageType) + 1:D4}-4000-8000-{revision:D12}";

    private static async Task SaveAndClearAsync(RuntimeFixture fixture)
    {
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    private static async Task<ProtocolOutboxRow> RowAsync(RuntimeFixture fixture, string messageId) =>
        await fixture.Context.ProtocolOutbox.AsNoTracking().SingleAsync(row => row.MessageId == messageId, Token);

    private static async Task<ProtocolOutboxRow[]> AllSnapshotRowsAsync(RuntimeFixture fixture) =>
        await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => SnapshotTypes.Contains(row.MessageType))
            .ToArrayAsync(Token);

    private static async Task<string[]> PendingSnapshotIdsAsync(RuntimeFixture fixture) =>
        await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => SnapshotTypes.Contains(row.MessageType) && row.AcknowledgedAt == null && row.FencedAt == null)
            .Select(row => row.MessageId)
            .ToArrayAsync(Token);

    private static async Task<string[]> PendingIdsOfAsync(RuntimeFixture fixture, string messageType) =>
        await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == messageType && row.AcknowledgedAt == null && row.FencedAt == null)
            .Select(row => row.MessageId)
            .ToArrayAsync(Token);

    private static async Task<DateTimeOffset?[]> FencedAtAsync(RuntimeFixture fixture) =>
        [.. (await AllSnapshotRowsAsync(fixture)).OrderBy(row => row.MessageId, StringComparer.Ordinal).Select(row => row.FencedAt)];

    /// <summary>主机启动时那几步拿到的服务：同一个库、夹具的钟。</summary>
    private static ServiceProvider HostServices(RuntimeFixture fixture)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(fixture.Clock);
        services.AddScoped(_ => new ControlServerDbContext(fixture.DbOptionsForTests));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("ControlServer.sln not found above the test output.");
    }
}
