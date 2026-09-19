using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 引擎每轮按任务类型准入（control-server#160）：一个固定站缺失不再让整张图失效（规格 21.2、REQ-0302、REQ-0342），
/// 站点任务类型准入种子由规则导出且内容哈希不变。
/// </summary>
public sealed class TaskTypeAdmissionRuntimeTests
{
    private const string DemandA = "10000000-0000-4000-8000-00000000000a";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 本票改动前 <c>fp/v2-impl@c9752b90</c> 上，同一测试工具第一轮写下的站点任务类型准入种子的内容哈希
    /// （<c>N1-1</c>、<c>N1-2_N1-3</c> 各配 <c>WIRE_TO_GATE</c>）。实测取得，不是按实现重算的。
    /// </summary>
    private const string SeedHashBeforeThisTicket = "ffd0252459db0a992d441c6e8ad4144e92ce720d4f5e0db7496cd9cf5aa4ae51";

    /// <summary>
    /// 目录里没有绑定站点（关卡 210 被别人从共用地图上删了）：目录完整读到即为一次完整确认，整张图不再记为
    /// <c>CandidateInvalid</c>；在途旅程照常推进，缺的站只落在该任务类型的准入原因上。
    /// </summary>
    [Fact]
    public async Task ABoundStationMissingFromTheCatalogNeitherInvalidatesTheMapNorStopsAJourneyUnderWay()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(DemandA, "SUBLOT-A", Now.AddMinutes(-5)));
        fixture.BoxCounts.Set("SUBLOT-A", 7);
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow accepted = await fixture.Context.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, accepted.Stage);

        fixture.Riot.SetMapStations(
            new RiotMapStation(12, "N1-1"),
            new RiotMapStation(13, "N1-2_N1-3"),
            new RiotMapStation(300, "等待点"));
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", accepted.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = accepted.PickupStationRiotId };
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);

        MapStationCatalogAvailability? catalog = await new CatalogAvailabilityStore(fixture.Context)
            .ReadStateAsync(25, Token);
        Assert.NotNull(catalog);
        Assert.NotEqual(MapStationCatalogState.CandidateInvalid, catalog.State);
        Assert.Null(catalog.LastFailureReason);
        Assert.Equal(
            JourneyRuntimeStage.AwaitingSublot,
            (await fixture.Context.JourneyRuntimes.AsNoTracking().SingleAsync(Token)).Stage);
    }

    /// <summary>
    /// 只有 <c>WIRE_TO_GATE</c> 可执行时，由规则导出的种子与改动前逐项相同：内容哈希不变，既有部署升级后不触发准入策略漂移。
    /// </summary>
    [Fact]
    public async Task TheAdmissionSeedDerivedFromTheRulesHashesExactlyAsBeforeWhileOnlyWireToGateIsExecutable()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();

        await fixture.Engine.ExecuteOnceAsync(Token);

        AdmissionPolicyStateRow state = await fixture.Context.AdmissionPolicyState.AsNoTracking().SingleAsync(Token);
        Assert.Equal(SeedHashBeforeThisTicket, state.ContentHash);
    }
}
