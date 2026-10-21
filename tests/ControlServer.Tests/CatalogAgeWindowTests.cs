using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Dashboard;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 目录时间窗（REQ-0366，CP-0020，control-server#827）：MesIngest 首次读到（目录项 <c>createdAt</c>）超过时间窗的未受理需求不派车，
/// 首次读到时刻缺失的同样不派；两者照旧留在积压里、带各自的原因码，不立结构性阻断、不升级防饥饿；已受理的（含放回待派的）不受影响。
/// </summary>
/// <remarks>
/// 整轮跑：引擎、派车轮、两个轮末汇总、SQLite（<see cref="RuntimeFixture"/>，一辆空闲车）。时钟是夹具的固定时钟，
/// 「超窗」都是按它摆的建单时刻，与真实时间无关。夹具里一条什么都不缺的需求第一轮就会被接走，所以「没接走」本身就是被挡的证据，
/// 每一处都再核对挡它的原因码。
/// </remarks>
public sealed class CatalogAgeWindowTests
{
    private const string Demand = "10000000-0000-4000-8000-000000000827";
    private const string Reissued = "10000000-0000-4000-8000-000000000828";
    private const string Sublot = "SUBLOT-827";
    private const string Zone = "MAP-25-WIRE_TO_GATE";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 判据：边界与配置 ------------------------------------------------------------------------------------

    /// <summary>29 分钟（默认窗内）照常派车。</summary>
    [Fact]
    public async Task ADemandFirstReadTwentyNineMinutesAgoIsDispatched()
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(TimeSpan.FromMinutes(29));

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal([Demand], await AcceptedAsync(fixture));
    }

    /// <summary>
    /// 31 分钟（默认窗外）不派：没有受理、没有订单意图；积压行带 <c>DEMAND_OLDER_THAN_CATALOG_WINDOW</c>、未受理。
    /// 取红：去掉判据，这一条被接走。
    /// </summary>
    [Fact]
    public async Task ADemandFirstReadThirtyOneMinutesAgoIsNotDispatchedAndWaitsWithItsReason()
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(TimeSpan.FromMinutes(31));

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Empty(await AcceptedAsync(fixture));
        Assert.Empty(await fixture.Context.OrderIntents.AsNoTracking().ToArrayAsync(Token));
        JourneyBacklogRow backlog = await fixture.BacklogAsync(Demand);
        Assert.Equal(DispatchReasonCodes.DemandOlderThanCatalogWindow, backlog.ReasonCode);
        Assert.Null(backlog.AcceptedAt);
    }

    /// <summary>
    /// 正面证明（调度 Coordinator13 要求）：同一目录里一新一旧，只派新的；旧的留在积压、带时间窗原因码、看板照样列出；
    /// 新的受理之后钟走过它的时间窗两小时，旅程照常、不被收回，旧的仍不派。
    /// </summary>
    [Fact]
    public async Task OnlyTheFreshDemandIsDispatchedAndItIsDeliveredPastItsWindow()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        const string stale = "10000000-0000-4000-8000-000000000829";
        fixture.Catalog.Set(
            fixture.Demand(stale, "SUBLOT-STALE", Now.AddMinutes(-31)),
            fixture.Demand(Demand, Sublot, Now.AddMinutes(-5)));
        fixture.BoxCounts.Set("SUBLOT-STALE", 4);
        fixture.BoxCounts.Set(Sublot, 4);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal([Demand], await AcceptedAsync(fixture));
        Assert.Equal(DispatchReasonCodes.DemandOlderThanCatalogWindow, (await fixture.BacklogAsync(stale)).ReasonCode);
        JourneyRuntimeRow journey = await fixture.Context.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        using (JsonDocument board = JsonDocument.Parse(JsonSerializer.Serialize(
                   await new DispatchBacklogQueryEndpoint().ReadAsync(fixture.Context, Token))))
        {
            JsonElement listed = Assert.Single(
                board.RootElement.GetProperty("backlog").EnumerateArray(),
                row => row.GetProperty("demandId").GetString() == stale);
            Assert.Equal(DispatchReasonCodes.DemandOlderThanCatalogWindow, listed.GetProperty("reasonCode").GetString());
        }

        for (int round = 0; round < 4; round++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(30));
            await fixture.Engine.ExecuteOnceAsync(Token);
        }

        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(Now.AddMinutes(120), fixture.Clock.GetUtcNow());
        JourneyRuntimeRow after = await fixture.Context.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        Assert.Equal(journey.JourneyId, after.JourneyId);
        Assert.NotEqual(JourneyRuntimeStage.Completed, after.Stage);
        Assert.NotEqual(
            DemandExecutionStatus.Cancelled,
            (await fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(Token)).Status);
        Assert.Equal([Demand], await AcceptedAsync(fixture));
        Assert.Equal(DispatchReasonCodes.DemandOlderThanCatalogWindow, (await fixture.BacklogAsync(stale)).ReasonCode);
    }

    /// <summary>恰好 30 分钟不算「已超过」（条文「已超过目录时间窗的」）：照常派车。</summary>
    [Fact]
    public async Task ADemandExactlyAtTheWindowIsStillDispatched()
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(TimeSpan.FromMinutes(30));

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal([Demand], await AcceptedAsync(fixture));
    }

    /// <summary>首次读到时刻缺失（目录项没有 <c>createdAt</c>）：不派，带另一个原因码。</summary>
    [Fact]
    public async Task ADemandWithoutAFirstReadTimeIsNotDispatchedAndWaitsWithItsOwnReason()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(Demand, Sublot, Now.AddMinutes(-1)) with { CreatedAt = default });
        fixture.BoxCounts.Set(Sublot, 4);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Empty(await AcceptedAsync(fixture));
        Assert.Equal(DispatchReasonCodes.DemandCatalogCreatedAtMissing, (await fixture.BacklogAsync(Demand)).ReasonCode);
    }

    /// <summary>时间窗按配置取值：配成 60 分钟，31 分钟的照常派。</summary>
    [Fact]
    public async Task TheWindowIsTheConfiguredOne()
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(TimeSpan.FromMinutes(31));
        fixture.Options.CatalogAgeWindow = TimeSpan.FromMinutes(60);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal([Demand], await AcceptedAsync(fixture));
    }

    /// <summary>
    /// 超窗后 MES 字段（含 DATES）、修订号都变了，首次读到时刻不变：仍然不派（不放回）。先断言字段真的变了、钟真的走了。
    /// </summary>
    [Fact]
    public async Task AnUpdateFromMesDoesNotPutADemandPastTheWindowBack()
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(TimeSpan.FromMinutes(31));
        await fixture.Engine.ExecuteOnceAsync(Token);
        AcceptedDemandSnapshot before = (await fixture.Catalog.ReadCatalogAsync(Token)).Items.Single();
        DateTimeOffset freshDates = fixture.Clock.GetUtcNow();
        AcceptedDemandSnapshot updated = before with
        {
            DemandRevision = before.DemandRevision + 1,
            ValueObservedAt = freshDates,
            LiveMesFields = before.LiveMesFields! with { MesSourceDate = freshDates, Step = "STEP-02" },
        };
        fixture.Catalog.Set(updated);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        await fixture.Engine.ExecuteOnceAsync(Token);
        // 决策事实变了的那一轮，积压行照既有规则记一次 DEMAND_DECISION_FACT_CHANGED（UpsertBacklog）；下一轮才写判据的结论。
        Assert.Empty(await AcceptedAsync(fixture));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.NotEqual(before.LiveMesFields, updated.LiveMesFields);
        Assert.Equal(before.CreatedAt, updated.CreatedAt);
        Assert.Empty(await AcceptedAsync(fixture));
        Assert.Equal(DispatchReasonCodes.DemandOlderThanCatalogWindow, (await fixture.BacklogAsync(Demand)).ReasonCode);
    }

    /// <summary>
    /// 需求离开目录后同键再现（REQ-0154 的新 DemandId、新的首次读到时刻）：按新时刻重新起算，照常派车；旧的那一条仍不派。
    /// </summary>
    [Fact]
    public async Task TheSameKeyBackUnderANewDemandIdIsTimedFromItsOwnFirstRead()
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(TimeSpan.FromMinutes(31));
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Empty(await AcceptedAsync(fixture));

        fixture.Catalog.Set(fixture.Demand(Reissued, Sublot, fixture.Clock.GetUtcNow().AddSeconds(-5)));
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal([Reissued], await AcceptedAsync(fixture));
    }

    /// <summary>
    /// 宿主经依赖注入装链，不经 <see cref="DispatchAdmissionCriteria.Default"/>：整轮夹具用的是后者，漏注册时它们照样绿（审查 M1）。
    /// 漏注册的宿主开轮不挡，超窗的需求会走到站点解析（60）及以后，可能立结构性阻断、挂人工搬运，违反条文。
    /// </summary>
    [Fact]
    public void TheHostRegistersTheCriterion()
    {
        ServiceCollection services = new();
        services.AddDispatchAdmission();

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IDispatchAdmissionCriterion)
            && descriptor.ImplementationType == typeof(CatalogAgeWindowCriterion));
    }

    /// <summary>
    /// 在途追加这条路也在受理前重读目录之后再判（审查 L1）：开轮时 9 分钟、判据放行、车决定追加；受理前重读那一刻钟走到 34 分钟，
    /// 放弃这次追加——旅程仍只挂第一条需求，积压行写时间窗的码。先断言重读那一刻确实到过。
    /// </summary>
    /// <remarks>
    /// 车队裁成一辆、本区允许途中追加，照 <c>AnInTransitVehicleTakesAnAppendedDemandIntoItsExistingJourney</c>：不拨钟时这条需求会被追加进去。
    /// <c>OnReread</c> 按目录的总读次数触发，第二轮开轮那一读也算「重读」，所以第一次触发时再挂一次，落在第二轮受理前那一读上。
    /// </remarks>
    [Fact]
    public async Task AnEnRouteAppendIsJudgedAgainWhenIntakeRereadsTheCatalog()
    {
        await using MultiVehicleExecutionTests.FleetFixture fleet = await MultiVehicleExecutionTests.FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..1], withRouteGraph: true);
        await fleet.AllowEnRouteAppendAsync(1_000_000);
        AcceptedDemandSnapshot first = MultiVehicleExecutionTests.FleetFixture.Demand(0, "N1-1", 0);
        fleet.Catalog.Set([first]);
        await fleet.RunRoundAsync();
        JourneyRuntimeRow journey = Assert.Single(await fleet.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(Token));

        AcceptedDemandSnapshot appended = MultiVehicleExecutionTests.FleetFixture.Demand(1, "N1-2", 1);
        fleet.Catalog.Set([first, appended]);
        bool intakeReread = false;
        fleet.Catalog.OnReread = () => fleet.Catalog.OnReread = () =>
        {
            intakeReread = true;
            fleet.Clock.Advance(TimeSpan.FromMinutes(25));
        };
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(1));

        Assert.True(intakeReread, "the round never reached intake's re-read of the catalog");
        Assert.Equal(journey.JourneyId, Assert.Single(await fleet.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(Token)).JourneyId);
        Assert.Equal(
            [first.DemandId],
            await fleet.Context.Set<JourneyDemandRow>().AsNoTracking().Select(row => row.DemandId).ToArrayAsync(Token));
        Assert.Equal(
            DispatchReasonCodes.DemandOlderThanCatalogWindow,
            (await fleet.Context.JourneyBacklog.AsNoTracking().SingleAsync(row => row.DemandId == appended.DemandId, Token)).ReasonCode);
    }

    /// <summary>
    /// 在途链从空闲链派生（REQ-0196：途中追加给在途车的候选也是未受理），所以同样有时间窗这一道。
    /// </summary>
    [Fact]
    public void TheInTransitChainCarriesTheWindowToo()
    {
        IReadOnlyList<IDispatchAdmissionCriterion> inTransit = DispatchAdmissionCriteria.InTransit(
            [new CatalogAgeWindowCriterion(), new VehicleDynamicFactsCriterion(Microsoft.Extensions.Options.Options.Create(new JourneyRuntimeOptions()))],
            Microsoft.Extensions.Options.Options.Create(new JourneyRuntimeOptions()));

        Assert.Contains(inTransit, criterion => criterion is CatalogAgeWindowCriterion);
    }

    // ---- 受理前重读目录时再判一次 ------------------------------------------------------------------------------

    /// <summary>
    /// 开轮时 29 分钟、判据放行；受理前重读目录那一刻钟已走到 31 分钟：放弃本次受理，积压行写时间窗原因码，没有旅程、没有订单意图。
    /// 第二次读目录就是受理前的重读（开轮读一次），先断言它确实发生了。
    /// </summary>
    [Fact]
    public async Task TheWindowIsJudgedAgainWhenIntakeRereadsTheCatalog()
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(TimeSpan.FromMinutes(29));
        int reads = 0;
        fixture.Catalog.BeforeRead = read =>
        {
            reads = read;
            if (read == 2)
            {
                fixture.Clock.Advance(TimeSpan.FromMinutes(2));
            }
        };

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.True(reads >= 2, $"intake never re-read the catalog (reads: {reads})");
        Assert.Empty(await AcceptedAsync(fixture));
        Assert.Empty(await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(await fixture.Context.OrderIntents.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(DispatchReasonCodes.DemandOlderThanCatalogWindow, (await fixture.BacklogAsync(Demand)).ReasonCode);
    }

    // ---- 已受理的不受影响 ----------------------------------------------------------------------------------

    /// <summary>
    /// 窗内受理的需求，钟走到首次读到之后两小时：旅程不被收回、需求不被取消、订单意图照旧，积压行仍记受理时刻。
    /// </summary>
    [Fact]
    public async Task AnAcceptedDemandIsDeliveredWhateverItsAge()
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(TimeSpan.FromMinutes(10));
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal([Demand], await AcceptedAsync(fixture));
        JourneyRuntimeRow journey = await fixture.Context.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        int intents = await fixture.Context.OrderIntents.CountAsync(Token);

        for (int round = 0; round < 4; round++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(30));
            await fixture.Engine.ExecuteOnceAsync(Token);
        }

        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(Now.AddMinutes(120), fixture.Clock.GetUtcNow());
        JourneyRuntimeRow after = await fixture.Context.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        Assert.Equal(journey.JourneyId, after.JourneyId);
        Assert.NotEqual(JourneyRuntimeStage.Completed, after.Stage);
        AcceptedDemandRow accepted = await fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(Token);
        Assert.NotEqual(DemandExecutionStatus.Cancelled, accepted.Status);
        Assert.Equal(intents, await fixture.Context.OrderIntents.CountAsync(Token));
        JourneyBacklogRow backlog = await fixture.BacklogAsync(Demand);
        Assert.NotNull(backlog.AcceptedAt);
        Assert.False(DispatchReasonCodes.IsCatalogAgeWindowRefusal(backlog.ReasonCode));
    }

    /// <summary>
    /// 放回待派、仍为已受理（CP-0020 新增项一说明 14；REQ-0211、REQ-0354、REQ-0328）：按释放服务的落库形状释放之后，
    /// 首次读到已过两小时，照样重新派车——时间窗按受理状态判，不按「此刻在积压里」判。
    /// </summary>
    [Fact]
    public async Task ADemandReleasedBackForRedispatchIsRedispatchedWhateverItsAge()
    {
        // 受理时把窗放大，让两小时前首次读到的需求能被接走；钟不拨，车载端事实保持新鲜。
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(TimeSpan.FromHours(2));
        fixture.Options.CatalogAgeWindow = TimeSpan.FromHours(3);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal([Demand], await AcceptedAsync(fixture));
        await ReleaseForRedispatchAsync(fixture);
        fixture.Options.CatalogAgeWindow = TimeSpan.FromMinutes(30);
        // 对照：同样两小时前首次读到、从未受理的一条，这一轮被时间窗挡下——证明窗确实回到了 30 分钟。
        fixture.Catalog.Set(
            (await fixture.Catalog.ReadCatalogAsync(Token)).Items.Single(),
            fixture.Demand(Reissued, "SUBLOT-CONTROL", fixture.Clock.GetUtcNow().AddHours(-2)));
        fixture.BoxCounts.Set("SUBLOT-CONTROL", 4);

        await fixture.Engine.ExecuteOnceAsync(Token);

        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(2, await fixture.Context.JourneyRuntimes.CountAsync(Token));
        Assert.False(await DemandJourneyLookup.ReleasedForRedispatch(fixture.Context).AnyAsync(Token));
        Assert.False(DispatchReasonCodes.IsCatalogAgeWindowRefusal((await fixture.BacklogAsync(Demand)).ReasonCode));
        Assert.Equal([Demand], await AcceptedAsync(fixture));
        Assert.Equal(DispatchReasonCodes.DemandOlderThanCatalogWindow, (await fixture.BacklogAsync(Reissued)).ReasonCode);
    }

    // ---- 不告警 ----------------------------------------------------------------------------------------------

    /// <summary>
    /// 超窗的需求越过防饥饿阈值（60 秒）也不升级，连跑三轮：防饥饿标记与日志都没有。
    /// </summary>
    [Fact]
    public async Task ADemandPastTheWindowIsNeverEscalatedForStarvation()
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(TimeSpan.FromMinutes(31));
        await fixture.ImportStarvationThresholdsAsync((Zone, 60));
        fixture.BoxCounts.Remove(Sublot);

        for (int round = 0; round < 3; round++)
        {
            await fixture.Engine.ExecuteOnceAsync(Token);
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        JourneyBacklogRow backlog = await fixture.BacklogAsync(Demand);
        Assert.Equal(DispatchReasonCodes.DemandOlderThanCatalogWindow, backlog.ReasonCode);
        Assert.Null(backlog.StarvationEscalatedAt);
        Assert.DoesNotContain(fixture.StarvationLog.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains(Demand, StringComparison.Ordinal));
    }

    /// <summary>
    /// 不立结构性阻断，窗内立过的随超窗清掉：AREA N22-1 在分区归属表里、地图上没有站点，窗内那一轮立 <c>AREA_STATION_NOT_FOUND</c>；
    /// 钟走过时间窗之后这条需求是有意不派，那条「请人工搬运」的阻断不再成立。
    /// </summary>
    [Fact]
    public async Task ADemandPastTheWindowRaisesNoStructuralBlockAndLosesTheOneItHad()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(Demand, Sublot, Now.AddMinutes(-29), "N22-1"));
        fixture.BoxCounts.Set(Sublot, 4);
        await fixture.Engine.ExecuteOnceAsync(Token);
        StructuralDispatchBlockRow raised = Assert.Single(
            await fixture.Context.Set<StructuralDispatchBlockRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Equal("AREA_STATION_NOT_FOUND", raised.ReasonCode);
        Assert.Null(raised.ClearedAt);

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Engine.ExecuteOnceAsync(Token);

        fixture.Context.ChangeTracker.Clear();
        Assert.All(
            await fixture.Context.Set<StructuralDispatchBlockRow>().AsNoTracking().ToArrayAsync(Token),
            block => Assert.NotNull(block.ClearedAt));
        Assert.Equal(DispatchReasonCodes.DemandOlderThanCatalogWindow, (await fixture.BacklogAsync(Demand)).ReasonCode);
    }

    // ---- 看板与分类 ------------------------------------------------------------------------------------------

    /// <summary>
    /// 看板积压照样列出超窗与缺首次读到时刻的需求，带原因码与中文说明——与 <c>DEMAND_LEFT_CATALOG</c> 不同，它们不从等车列表消失，
    /// 也不算静默；两码都是普通积压，不立结构性阻断。
    /// </summary>
    [Theory]
    [InlineData(DispatchReasonCodes.DemandOlderThanCatalogWindow, "时间窗")]
    [InlineData(DispatchReasonCodes.DemandCatalogCreatedAtMissing, "首次读到")]
    public async Task TheDashboardListsBothRefusalsWithAChineseDescription(string reasonCode, string descriptionMentions)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Context.JourneyBacklog.Add(new JourneyBacklogRow
        {
            DemandId = Demand,
            TransportDemandKey = $"{Sublot}|WIRE_TO_GATE",
            FirstSeenAt = Now.AddMinutes(-40),
            DemandCreatedAt = Now.AddMinutes(-41),
            DecisionFingerprint = "fingerprint",
            ReasonCode = reasonCode,
            LastSeenAt = Now,
        });
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        using JsonDocument fact = JsonDocument.Parse(JsonSerializer.Serialize(
            await new DispatchBacklogQueryEndpoint().ReadAsync(fixture.Context, Token)));

        Assert.True(DispatchBacklogQueryEndpoint.Descriptions.TryGetValue(reasonCode, out string? description));
        Assert.Matches(@"\p{IsCJKUnifiedIdeographs}", description);
        Assert.Contains(descriptionMentions, description, StringComparison.Ordinal);
        JsonElement row = Assert.Single(fact.RootElement.GetProperty("backlog").EnumerateArray());
        Assert.Equal(reasonCode, row.GetProperty("reasonCode").GetString());
        Assert.Equal(description, row.GetProperty("reasonDescription").GetString());
        Assert.Equal(0, fact.RootElement.GetProperty("silentBacklogCount").GetInt32());
        Assert.Equal(DispatchReasonClass.Backlog, StructuralDispatchClassification.ClassOf(reasonCode));
        Assert.False(DispatchReasonCodes.IsSilent(reasonCode));
        Assert.True(DispatchReasonCodes.IsCatalogAgeWindowRefusal(reasonCode));
    }

    // ---- 配置 ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 默认 30 分钟、必须为正（0 与负数拒绝）、至多 24 小时，出厂配置写明这一项。上限挡的是 <c>"30"</c> 这种写法：
    /// .NET 把它绑定成 30 天，时间窗等于没有（审查 L4）。
    /// </summary>
    [Fact]
    public void TheWindowDefaultsToThirtyMinutesMustBePositiveAtMostADayAndIsShippedInAppSettings()
    {
        const string failure = "CatalogAgeWindow must be positive and at most 24 hours.";
        Assert.Equal(TimeSpan.FromMinutes(30), new JourneyRuntimeOptions().CatalogAgeWindow);
        JourneyRuntimeOptionsValidator validator = new(new ConfigurationBuilder().Build());
        JourneyRuntimeOptions options = JourneyRuntimeOptionsTests.ValidEnabledOptions();
        foreach (TimeSpan accepted in new[] { TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(30), TimeSpan.FromHours(24) })
        {
            options.CatalogAgeWindow = accepted;
            Assert.DoesNotContain(failure, validator.Validate(null, options).Failures ?? [], StringComparer.Ordinal);
        }
        TimeSpan thirtyAsWritten = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["JourneyRuntime:catalogAgeWindow"] = "30" })
            .Build()
            .GetSection("JourneyRuntime").Get<JourneyRuntimeOptions>()!.CatalogAgeWindow;
        Assert.Equal(TimeSpan.FromDays(30), thirtyAsWritten);
        foreach (TimeSpan refused in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-1), TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1), thirtyAsWritten })
        {
            options.CatalogAgeWindow = refused;
            Assert.Contains(failure, validator.Validate(null, options).Failures ?? [], StringComparer.Ordinal);
        }

        IConfiguration shipped = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), "src", "ControlServer.Host", "appsettings.json"), optional: false)
            .Build();
        Assert.Equal("00:30:00", shipped["JourneyRuntime:catalogAgeWindow"]);
    }

    /// <summary>L2 编排把 setup 的 <c>CatalogAgeWindow</c> 映射成这个环境变量，与 <c>StationTimeoutRedispatchLimit</c> 同一种做法。</summary>
    [Fact]
    public void TheWindowBindsFromTheEnvironmentVariableTheL2OrchestratorSets()
    {
        const string prefix = "CONTROL_SERVER_TEST_CS827_WINDOW_";
        Environment.SetEnvironmentVariable(prefix + "JourneyRuntime__CatalogAgeWindow", "02:00:00");
        try
        {
            IConfiguration configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();

            Assert.Equal(
                TimeSpan.FromHours(2),
                configuration.GetSection("JourneyRuntime").Get<JourneyRuntimeOptions>()!.CatalogAgeWindow);
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "JourneyRuntime__CatalogAgeWindow", null);
        }
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------

    private static async Task<RuntimeFixture> FixtureWithDemandAsync(TimeSpan firstReadAgo)
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(Demand, Sublot, fixture.Clock.GetUtcNow() - firstReadAgo));
        fixture.BoxCounts.Set(Sublot, 4);
        return fixture;
    }

    private static async Task<string[]> AcceptedAsync(RuntimeFixture fixture) =>
        await fixture.Context.AcceptedDemands.AsNoTracking().Select(row => row.DemandId).ToArrayAsync(Token);

    /// <summary>
    /// 释放的落库形状，同 <c>MultiVehicleExecutionTests.AClaimOnARedispatchedDemandTheSegmentNeverMadeGoodOnIsWithdrawn</c>：
    /// 归属标 RELEASED_FOR_REDISPATCH，旅程按释放服务同一条路关闭，积压行清掉受理时刻。
    /// </summary>
    private static async Task ReleaseForRedispatchAsync(RuntimeFixture fixture)
    {
        JourneyRuntimeRow journey = await fixture.Context.JourneyRuntimes.SingleAsync(Token);
        JourneyDemandRow membership = await fixture.Context.Set<JourneyDemandRow>().SingleAsync(Token);
        membership.RemovedAt = fixture.Clock.GetUtcNow();
        membership.RemovalReason = DemandJourneyLookup.ReleasedForRedispatchReason;
        await new PickupStopTermination(fixture.Context)
            .StageJourneyClosureAsync(journey, DemandJourneyLookup.ReleasedForRedispatchReason, fixture.Clock.GetUtcNow(), Token);
        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.SingleAsync(row => row.DemandId == Demand, Token);
        backlog.AcceptedAt = null;
        backlog.ReasonCode = DemandJourneyLookup.ReleasedForRedispatchReason;
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        Assert.True(await DemandJourneyLookup.ReleasedForRedispatch(fixture.Context).AnyAsync(row => row.DemandId == Demand, Token));
    }
}
