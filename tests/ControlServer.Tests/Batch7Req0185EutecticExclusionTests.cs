using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// REQ-0185「共晶与低温共晶在选任务阶段排除」的承载机制，核实并钉住（批次7-09，control-server#214）。
/// </summary>
/// <remarks>
/// <para>
/// <b>承载它的是分区归属表，不是一张独立的排除列表。</b>用户 2026-09-19 定：不新建
/// <c>TransportExecutionExcludedArea</c> 列表，靠分区归属表不收录共晶、低温共晶机台来排除；现场前提写成「分区归属表不收录
/// 这两类机台，RIoT 地图上也不放它们的站点」。这里整轮实测三种组合，第一种是 REQ-0185 本身：
/// </para>
/// <list type="bullet">
/// <item>a）分区归属表不收录（不论地图上有没有站点）：<c>OUT_OF_SCOPE_AREA</c>，需求留在积压，不派车、不装货、不告警——
/// 结构性阻断不立、防饥饿升级也不发（这个 AREA 没有分区，也就没有阈值）。</item>
/// <item>b）分区归属表收录、地图上没有站点：<c>AREA_STATION_NOT_FOUND</c>，立即形成结构性派车阻断告警。这是已知且接受的行为，
/// 不改原因码归类；它是「只删地图站点、分区表仍收录」的后果，运维说明里点名了。</item>
/// <item>c）两者都收录：照常受理——排除完全来自前两层，没有第三处按名字挡它。</item>
/// </list>
/// <para>
/// <c>E9-1</c> 是一个替身 AREA，代表一台共晶或低温共晶机台；它的名字不参与任何判断，这正是要证的（排除只看表）。
/// </para>
/// </remarks>
public sealed class Batch7Req0185EutecticExclusionTests
{
    private const string Demand = "10000000-0000-4000-8000-000000000185";
    private const string Sublot = "SUBLOT-EUTECTIC";
    private const string EutecticArea = "E9-1";
    private const string Zone = "MAP-25-WIRE_TO_GATE";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 组合 a，REQ-0185 本身：分区归属表不收录的共晶类 AREA，地图上有没有它的站点都一样——留在积压、原因码
    /// <c>OUT_OF_SCOPE_AREA</c>、不派车、不装货、不告警。阈值配了、需求等了一整天，防饥饿升级也不发。
    /// </summary>
    [Theory]
    [Trait("Requirement", "REQ-0185")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Req0185AnEutecticAreaTheAssignmentTableLeavesOutStaysInTheBacklogSilentlyWhateverTheMapHas(
        bool mapHasItsStation)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        SetMap(fixture, mapHasItsStation);
        await fixture.ImportStarvationThresholdsAsync((Zone, 60));
        fixture.Catalog.Set(fixture.Demand(Demand, Sublot, fixture.Clock.GetUtcNow().AddDays(-1), EutecticArea));
        fixture.BoxCounts.Set(Sublot, 4);

        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyBacklogRow backlog = await fixture.BacklogAsync(Demand);
        Assert.Equal("OUT_OF_SCOPE_AREA", backlog.ReasonCode);
        Assert.Null(backlog.AcceptedAt);
        Assert.Null(backlog.StarvationEscalatedAt);
        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(Token));
        Assert.Empty(await fixture.Context.OrderIntents.ToArrayAsync(Token));
        Assert.Empty(await fixture.Context.StationOperations.ToArrayAsync(Token));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        Assert.Empty(await fixture.Context.Set<StructuralDispatchBlockRow>().ToArrayAsync(Token));
        Assert.DoesNotContain(fixture.StructuralBlockLog.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.DoesNotContain(fixture.StarvationLog.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    /// <summary>
    /// 组合 b：分区归属表收录了、地图上没有站点——<c>AREA_STATION_NOT_FOUND</c>，立即结构性阻断告警。已知且接受；
    /// 现场「只删地图站点、分区表仍收录」就会常年挂着这条告警。
    /// </summary>
    [Fact]
    public async Task AnAreaTheTableNamesButTheMapLacksRaisesAStructuralBlockAtOnce()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await AssignEutecticAreaAsync(fixture);
        SetMap(fixture, mapHasItsStation: false);
        fixture.Catalog.Set(fixture.Demand(Demand, Sublot, fixture.Clock.GetUtcNow().AddMinutes(-1), EutecticArea));
        fixture.BoxCounts.Set(Sublot, 4);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal("AREA_STATION_NOT_FOUND", (await fixture.BacklogAsync(Demand)).ReasonCode);
        StructuralDispatchBlockRow block = Assert.Single(
            await fixture.Context.Set<StructuralDispatchBlockRow>().ToArrayAsync(Token));
        Assert.Equal(("AREA_STATION_NOT_FOUND", Demand), (block.ReasonCode, block.DemandId));
        Assert.Contains(fixture.StructuralBlockLog.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(Token));
    }

    /// <summary>组合 c：两者都收录就照常受理——除了分区归属表与地图站点，没有别处按名字挡它。</summary>
    [Fact]
    public async Task AnAreaTheTableNamesAndTheMapCarriesIsDispatchedAsUsual()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await AssignEutecticAreaAsync(fixture);
        SetMap(fixture, mapHasItsStation: true);
        fixture.Catalog.Set(fixture.Demand(Demand, Sublot, fixture.Clock.GetUtcNow().AddMinutes(-1), EutecticArea));
        fixture.BoxCounts.Set(Sublot, 4);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal([Demand], await fixture.Context.AcceptedDemands.Select(row => row.DemandId).ToArrayAsync(Token));
        Assert.Empty(await fixture.Context.Set<StructuralDispatchBlockRow>().ToArrayAsync(Token));
    }

    private static void SetMap(RuntimeFixture fixture, bool mapHasItsStation) =>
        fixture.Riot.SetMapStations(
        [
            new RiotMapStation(12, "N1-1"),
            new RiotMapStation(13, "N1-2_N1-3"),
            new RiotMapStation(210, "关卡"),
            new RiotMapStation(300, "等待点"),
            .. mapHasItsStation ? [new RiotMapStation(19, EutecticArea)] : Array.Empty<RiotMapStation>(),
        ]);

    private static Task<AreaAssignmentTableVersion> AssignEutecticAreaAsync(RuntimeFixture fixture) =>
        fixture.ImportAreaAssignmentsAsync(
        [
            .. RuntimeFixture.DefaultAssignedAreas.Select(area => new AreaAssignment(area, Zone, "FRONT")),
            new AreaAssignment(EutecticArea, Zone, "FRONT"),
        ]);
}
