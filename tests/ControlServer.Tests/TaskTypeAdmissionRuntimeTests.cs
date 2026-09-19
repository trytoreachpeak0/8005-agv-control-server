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

    private const string OtherTaskTypeDemand = "10000000-0000-4000-8000-00000000000b";

    private static readonly TaskTypeStationBinding StagingBinding =
        new(TransportTaskTypes.StagingToWire, 305, "派工待送取货", "SITE-CHECK-STAGING");

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
    [Trait("IntegrationSlice", "FP-IS-10")]
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
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", accepted.PickupUpperId, accepted.PickupStationRiotId);
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
    /// 引擎受理时冻结当时生效的规则版本、绑定集版本与解析出的固定站；之后绑定改了、服务重启了，这条需求仍按冻结的
    /// 关卡 210 建关卡腿，冻结行不被改写（REQ-0344）。同一需求不会在后续轮次里再被准入、再被冻结一次。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task TheAcceptanceFreezesTheVersionsAndTheStationAndARebindingAndARestartChangeNeither()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Riot.SetMapStations(
            new RiotMapStation(12, "N1-1"),
            new RiotMapStation(13, "N1-2_N1-3"),
            new RiotMapStation(210, "关卡"),
            new RiotMapStation(220, "关卡2"),
            new RiotMapStation(300, "等待点"));
        TaskTypeStationBindingSetVersion active = (await ActiveBindingsAsync(fixture))!;
        fixture.Catalog.Set(fixture.Demand("10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await fixture.Engine.ExecuteOnceAsync(Token);

        DemandTaskTypeStationFreeze expected = new(
            "10000000-0000-4000-8000-000000000001", active.RuleVersion, 25, active.Version, Now);
        Assert.Equal(expected, await FreezeAsync(fixture));
        FrozenStationFact dropoff = Assert.Single(
            await new CatalogAvailabilityStore(fixture.Context).ReadFrozenStationsAsync(expected.DemandId, Token),
            station => station.Role == FrozenStationRole.Dropoff);
        Assert.Equal(new FrozenStationFact(FrozenStationRole.Dropoff, 25, 210, "关卡"), dropoff);

        // The binding moves to 220 and the server restarts; the journey goes on to its gate leg.
        await TaskTypeStationRuntimeSeed.ActivateAsync(
            fixture.DbOptionsForTests,
            Now,
            bindings: [TaskTypeStationRuntimeSeed.GateBinding with { StationRiotId = 220, StationName = "关卡2" }]);
        Assert.NotEqual(active.Version, (await ActiveBindingsAsync(fixture))!.Version);
        await fixture.RecreateEngineAsync();
        JourneyRuntimeRow runtime = await fixture.AdvanceToGateArrivalAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, runtime.Stage);
        OrderIntentRow gateLeg = await fixture.Context.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.UpperId == runtime.GateUpperId, Token);
        Assert.Equal(210, gateLeg.DestinationStationId);
        Assert.Equal(expected, await FreezeAsync(fixture));
        Assert.Equal(2, await fixture.Context.Set<ConfigurationConsumerBindingRow>().AsNoTracking()
            .CountAsync(row => row.ConsumerId == expected.DemandId &&
                row.ObjectKind != GovernedObjectKind.DispatchZoneAreaAssignment, Token));
    }

    /// <summary>
    /// 规则表前进了一版（预置里的规则改了），生效绑定集还停在它当初校验用的那版规则上：新需求照常受理，冻结的是
    /// 生效绑定集自带的规则版本，不是最新规则版本——#159 的冻结端口要求两者成对（#161 审查交接）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task ANewerRuleVersionWithTheActiveBindingSetUnchangedStillFreezesTheBindingSetsOwnRuleVersion()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        TaskTypeStationBindingSetVersion active = (await ActiveBindingsAsync(fixture))!;
        await using (ControlServerDbContext writer = new(fixture.DbOptionsForTests))
        {
            TaskTypeStationRuleVersion newer = (await TaskTypeStationRuntimeSeed.Access(writer).Rules.WriteVersionAsync(
                [.. TaskTypeStationTestData.SixRules.Where(rule => rule.TaskType != TransportTaskTypes.DieToOven)],
                "preset:test-newer",
                Now,
                Token)).Version;
            Assert.True(newer.Version > active.RuleVersion);
        }
        Assert.Equal(active.Version, (await ActiveBindingsAsync(fixture))!.Version);
        fixture.Catalog.Set(fixture.Demand("10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(
            new DemandTaskTypeStationFreeze("10000000-0000-4000-8000-000000000001", active.RuleVersion, 25, active.Version, Now),
            await FreezeAsync(fixture));
    }

    /// <summary>
    /// 一次受理在最后一刻被拒（需求在受理前的复读里不见了），之后改绑到 220：下一轮照常受理这条需求、冻结新的落点站，
    /// 引擎这一轮不抛异常。落点站若在受理事务之外先冻结，被拒那次会留下 210 的冻结行，下一轮改写被拒、异常冒出
    /// <c>ExecuteOnceAsync</c>，从此每一轮都失败，所有任务类型都不再受理（#160 审查应修第 2 条）。
    /// </summary>
    /// <remarks>
    /// control-server#198：同一轮里另有一条别的任务类型的需求（<c>STAGING_TO_WIRE</c>，AREA 在图上没有站点），它这一轮
    /// 照常得到自己的判定——结构性的 <c>AREA_STATION_NOT_FOUND</c>，由轮末汇总立成一条结构性派车阻断。夹具只有一辆车，两条
    /// 需求不能同轮受理，所以看的是轮末汇总：在缺陷版本上这一轮在 A 的冻结处中断，汇总走不到，B 的判定就到不了。B 的断言
    /// 排在「本轮不抛异常」与原有断言之前，缺陷版本红在它上面。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task AnAcceptanceRefusedAtTheLastMomentLeavesNothingFrozenSoARebindingIsTakenNextRound()
    {
        const string demandId = "10000000-0000-4000-8000-000000000001";
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Riot.SetMapStations(
            new RiotMapStation(12, "N1-1"),
            new RiotMapStation(13, "N1-2_N1-3"),
            new RiotMapStation(210, "关卡"),
            new RiotMapStation(220, "关卡2"),
            new RiotMapStation(300, "等待点"));
        AcceptedDemandSnapshot demand = fixture.Demand(demandId, "SUBLOT-001", Now.AddMinutes(-10));
        fixture.Catalog.Set(demand);
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        // Read 1 is discovery, read 2 the intake's final re-read: the demand is gone by then.
        fixture.Catalog.BeforeRead = readCount =>
        {
            if (readCount == 2)
            {
                fixture.Catalog.Set();
            }
        };
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(
            "FINAL_CATALOG_CANDIDATE_GONE",
            (await fixture.Context.JourneyBacklog.AsNoTracking().SingleAsync(row => row.DemandId == demandId, Token)).ReasonCode);
        Assert.False(await fixture.Context.AcceptedDemands.AnyAsync(row => row.DemandId == demandId, Token));

        fixture.Catalog.BeforeRead = null;
        // Another task type's demand joins the round: STAGING_TO_WIRE, bound and allowed, at an AREA the Map has no station
        // for. Younger than the first, so the one vehicle is offered the first.
        AcceptedDemandSnapshot other = fixture.Demand(OtherTaskTypeDemand, "SUBLOT-002", Now.AddMinutes(-5), area: "N22-1") with
        {
            WorkType = TransportTaskTypes.StagingToWire,
            TransportDemandKey = $"SUBLOT-002|{TransportTaskTypes.StagingToWire}",
        };
        fixture.Catalog.Set(demand, other);
        fixture.BoxCounts.Set("SUBLOT-002", 7);
        fixture.Options.AllowedWorkTypes = [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire];
        fixture.Riot.SetMapStations(
            new RiotMapStation(12, "N1-1"),
            new RiotMapStation(13, "N1-2_N1-3"),
            new RiotMapStation(210, "关卡"),
            new RiotMapStation(220, "关卡2"),
            new RiotMapStation(300, "等待点"),
            new RiotMapStation(StagingBinding.StationRiotId, StagingBinding.StationName));
        (long ruleVersion, long bindingSetVersion) = await TaskTypeStationRuntimeSeed.ActivateAsync(
            fixture.DbOptionsForTests,
            Now,
            requiredTaskTypes: [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire],
            bindings: [TaskTypeStationRuntimeSeed.GateBinding with { StationRiotId = 220, StationName = "关卡2" }, StagingBinding]);
        fixture.Context.ChangeTracker.Clear();
        Exception? thrown = await Record.ExceptionAsync(() => fixture.Engine.ExecuteOnceAsync(Token));

        fixture.Context.ChangeTracker.Clear();
        Assert.Equal("AREA_STATION_NOT_FOUND", (await fixture.BacklogAsync(OtherTaskTypeDemand)).ReasonCode);
        Assert.Contains(
            await new StructuralDispatchBlockStore(fixture.Context).ListUnclearedAsync(Token),
            block => block.DemandId == OtherTaskTypeDemand && block.ReasonCode == "AREA_STATION_NOT_FOUND");
        Assert.Null(thrown);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        FrozenStationFact dropoff = Assert.Single(
            await new CatalogAvailabilityStore(fixture.Context).ReadFrozenStationsAsync(demandId, Token),
            station => station.Role == FrozenStationRole.Dropoff);
        Assert.Equal(new FrozenStationFact(FrozenStationRole.Dropoff, 25, 220, "关卡2"), dropoff);
        Assert.Equal(new DemandTaskTypeStationFreeze(demandId, ruleVersion, 25, bindingSetVersion, Now), await FreezeAsync(fixture));
    }

    /// <summary>
    /// 准入种子与解析器读同一版规则：生效绑定集所依据的那版，不是最新一版。规则表前进了一版而生效绑定集没换时，种子
    /// 内容不变，不被判成准入策略漂移（#188 审查顺手改）。这里的新规则去掉了 <c>STAGING_TO_WIRE</c>，读最新规则的话
    /// 种子会少掉它、哈希随之不同。
    /// </summary>
    /// <remarks>
    /// control-server#163 起种子不再看固定端，#160 时的构造（新规则把 <c>WIRE_TO_GATE</c> 的固定端改成起点）读哪版
    /// 都得出同一个种子，区分不出来，所以改成去掉一个可执行的任务类型；比对对象是同一夹具不写新规则时的种子哈希。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task TheAdmissionSeedReadsTheRulesTheActiveBindingSetWasBuiltOnNotTheLatest()
    {
        string baseline;
        await using (RuntimeFixture unchanged = await RuntimeFixture.CreateAsync())
        {
            await unchanged.Engine.ExecuteOnceAsync(Token);
            baseline = (await unchanged.Context.AdmissionPolicyState.AsNoTracking().SingleAsync(Token)).ContentHash;
        }
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await using (ControlServerDbContext writer = new(fixture.DbOptionsForTests))
        {
            await TaskTypeStationRuntimeSeed.Access(writer).Rules.WriteVersionAsync(
                [.. TaskTypeStationTestData.SixRules.Where(rule => rule.TaskType != TransportTaskTypes.StagingToWire)],
                "preset:test-newer",
                Now,
                Token);
        }

        await fixture.Engine.ExecuteOnceAsync(Token);

        AdmissionPolicyStateRow state = await fixture.Context.AdmissionPolicyState.AsNoTracking().SingleAsync(Token);
        Assert.Equal(baseline, state.ContentHash);
    }

    private static async Task<TaskTypeStationBindingSetVersion?> ActiveBindingsAsync(RuntimeFixture fixture) =>
        await TaskTypeStationRuntimeSeed.Access(fixture.Context).Bindings.ReadActiveAsync(25, Token);

    private static async Task<DemandTaskTypeStationFreeze?> FreezeAsync(RuntimeFixture fixture)
    {
        fixture.Context.ChangeTracker.Clear();
        return await new DemandTaskTypeStationFreezeStore(fixture.Context)
            .ReadAsync("10000000-0000-4000-8000-000000000001", Token);
    }

    /// <summary>
    /// 种子把每个 AREA 命名的机台站配给本构建可执行的每个任务类型，不论固定端（control-server#163）：机台站在
    /// <c>WIRE_TO_GATE</c> 里是取货端，在 <c>STAGING_TO_WIRE</c> 里是卸货端，两种都在那里判准入。
    /// </summary>
    /// <remarks>
    /// control-server#160 时这里钉的是「只有 <c>WIRE_TO_GATE</c> 可执行时哈希与改动前相同」。#163 让
    /// <c>STAGING_TO_WIRE</c> 可执行，前提不再成立：内容变了，哈希也不再是 <see cref="SeedHashBeforeThisTicket"/>。所以出厂的
    /// <c>admissionPolicyVersion</c> 升过绑定旧哈希的版本 1（<see cref="ReversedDirectionJourneyRuntimeTests"/> 里有一条钉它），
    /// 已部署的实例升级后按新版本导入，不判漂移。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task TheAdmissionSeedPairsEveryAreaStationWithEveryExecutableTaskType()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(
            [
                ("N1-1", TransportTaskTypes.StagingToWire),
                ("N1-1", TransportTaskTypes.WireToGate),
                ("N1-2_N1-3", TransportTaskTypes.StagingToWire),
                ("N1-2_N1-3", TransportTaskTypes.WireToGate),
            ],
            (await fixture.Context.StationTaskTypeAdmissions.AsNoTracking().ToArrayAsync(Token))
                .Select(row => (row.StationId, row.TaskType))
                .OrderBy(pair => pair.StationId, StringComparer.Ordinal)
                .ThenBy(pair => pair.TaskType, StringComparer.Ordinal));
        AdmissionPolicyStateRow state = await fixture.Context.AdmissionPolicyState.AsNoTracking().SingleAsync(Token);
        Assert.NotEqual(SeedHashBeforeThisTicket, state.ContentHash);
    }
}
