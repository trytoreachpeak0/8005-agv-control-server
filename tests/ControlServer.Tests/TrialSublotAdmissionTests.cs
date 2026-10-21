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
/// 试验机批号（REQ-0368，CP-0023，control-server#828）：未受理候选的 SUBLOT 按目录原样逐字符比较，配置的字符位（从 1 计、开头的 Q 算第 1 个）
/// 落在配置的取值里的，不派车、不受理；照旧留在目录与积压里、带原因码，不立结构性阻断、不升级防饥饿；已受理的（含放回待派的）不受影响；
/// 配置为空不判。
/// </summary>
/// <remarks>
/// 整轮跑：引擎、派车轮、两个轮末汇总、SQLite（<see cref="RuntimeFixture"/>，一辆空闲车），规则开成生产值（第 6 个字符、<c>9</c>）。
/// 夹具里一条什么都不缺的需求第一轮就会被接走，所以「没接走」本身就是被挡的证据，每一处都再核对挡它的原因码。
/// 样本批号取自票面（MES 样本 7-24，与 10 月现场）。
/// </remarks>
public sealed class TrialSublotAdmissionTests
{
    private const string Demand = "10000000-0000-4000-8000-000000000928";
    private const string Other = "10000000-0000-4000-8000-000000000929";
    private const string TrialSublot = "Q26079458-1";
    private const string Zone = "MAP-25-WIRE_TO_GATE";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 规则本身：样本批号 ----------------------------------------------------------------------------------

    /// <summary>
    /// 第 6 个字符是半角 9 的才算：三条样本试验批（含流水号带字母的 <c>Q26069E34-1</c>）判中；9 月批次 <c>Q26091200-3</c>（第 5 个字符是 9）、
    /// 10 月 <c>Q26102047-1</c>、第 6 位是全角「９」、前面多一个空格导致错位、长度不够第 6 个字符、空串都不判。
    /// </summary>
    [Theory]
    [InlineData("Q26079458-1", true)]
    [InlineData("Q26079308-1", true)]
    [InlineData("Q26069E34-1", true)]
    [InlineData("Q26079", true)]
    [InlineData("Q26091200-3", false)]
    [InlineData("Q26102047-1", false)]
    [InlineData("Q2607９458-1", false)]
    [InlineData(" Q26079458-1", false)]
    [InlineData("Q2609", false)]
    [InlineData("", false)]
    public void TheProductionRuleMatchesCharacterSixBeingAHalfWidthNine(string sublot, bool trial)
    {
        Assert.Equal(trial, ProductionRule().Matches(sublot));
    }

    /// <summary>配置为空不判：字符位为 0 或取值为空，样本试验批也不判中。</summary>
    [Fact]
    public void AnEmptyRuleMatchesNothing()
    {
        Assert.False(TrialSublotRule.From(new JourneyRuntimeOptions()).Matches(TrialSublot));
        Assert.False(new TrialSublotRule(0, "9").Matches(TrialSublot));
        Assert.False(new TrialSublotRule(6, "").Matches(TrialSublot));
        Assert.False(TrialSublotRule.Off.Matches(TrialSublot));
    }

    /// <summary>取值是集合：配成 <c>"89"</c>，第 6 个字符是 8 或 9 都判中，7 不判。</summary>
    [Fact]
    public void EveryCharacterOfTheValuesIsOneValue()
    {
        TrialSublotRule rule = TrialSublotRule.From(new JourneyRuntimeOptions
        {
            TrialSublotCharacterPosition = 6,
            TrialSublotCharacters = "89",
        });

        Assert.True(rule.Matches("Q26078458-1"));
        Assert.True(rule.Matches("Q26079458-1"));
        Assert.False(rule.Matches("Q26077458-1"));
    }

    // ---- 整轮：正例不派、反例照派 ------------------------------------------------------------------------------

    /// <summary>
    /// 样本试验批不派：没有受理、没有订单意图；积压行带 <c>DEMAND_TRIAL_SUBLOT</c>、未受理，需求仍在目录里。
    /// 取红：去掉判据，这一条被接走。
    /// </summary>
    [Theory]
    [InlineData("Q26079458-1")]
    [InlineData("Q26079308-1")]
    [InlineData("Q26069E34-1")]
    public async Task ATrialSublotIsNotDispatchedAndWaitsWithItsReason(string sublot)
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(sublot);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Empty(await AcceptedAsync(fixture));
        Assert.Empty(await fixture.Context.OrderIntents.AsNoTracking().ToArrayAsync(Token));
        JourneyBacklogRow backlog = await fixture.BacklogAsync(Demand);
        Assert.Equal(DispatchReasonCodes.DemandTrialSublot, backlog.ReasonCode);
        Assert.Null(backlog.AcceptedAt);
        Assert.Contains((await fixture.Catalog.ReadCatalogAsync(Token)).Items, item => item.DemandId == Demand);
    }

    /// <summary>反例照常派车：9 月批次、10 月批次、全角「９」、前导空格错位。</summary>
    [Theory]
    [InlineData("Q26091200-3")]
    [InlineData("Q26102047-1")]
    [InlineData("Q2607９458-1")]
    [InlineData(" Q26079458-1")]
    public async Task ASublotThatIsNotATrialOneIsDispatched(string sublot)
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(sublot);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal([Demand], await AcceptedAsync(fixture));
    }

    /// <summary>配置为空不滤（出厂默认）：同一条样本试验批照常派车。</summary>
    [Fact]
    public async Task WithTheRuleLeftEmptyATrialSublotIsDispatched()
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(TrialSublot, ruleOn: false);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal([Demand], await AcceptedAsync(fixture));
    }

    /// <summary>
    /// 同一目录里一条试验批、一条正常批：只派正常的；试验批留在积压、看板照样列出，带原因码。
    /// </summary>
    [Fact]
    public async Task OnlyTheOrdinaryDemandIsDispatchedAndTheTrialOneIsListedOnTheDashboard()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        TurnRuleOn(fixture);
        fixture.Catalog.Set(
            fixture.Demand(Demand, TrialSublot, Now.AddMinutes(-2)),
            fixture.Demand(Other, "Q26102047-1", Now.AddMinutes(-1)));
        fixture.BoxCounts.Set(TrialSublot, 4);
        fixture.BoxCounts.Set("Q26102047-1", 4);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal([Other], await AcceptedAsync(fixture));
        using JsonDocument board = JsonDocument.Parse(JsonSerializer.Serialize(
            await new DispatchBacklogQueryEndpoint().ReadAsync(fixture.Context, Token)));
        JsonElement listed = Assert.Single(
            board.RootElement.GetProperty("backlog").EnumerateArray(),
            row => row.GetProperty("demandId").GetString() == Demand);
        Assert.Equal(DispatchReasonCodes.DemandTrialSublot, listed.GetProperty("reasonCode").GetString());
    }

    /// <summary>
    /// 既是试验批又超过目录时间窗（REQ-0366）：试验批先判（判据序号在时间窗之前），不派，不立结构性阻断。
    /// 一辆车的夹具里积压行写的就是试验批的码；车多时那一行可能是某辆车自己的码（见 <see cref="CatalogAgeWindowCriterion"/>），
    /// 所以这里只在一辆车时比对码。
    /// </summary>
    [Fact]
    public async Task ATrialSublotPastTheCatalogWindowIsRefusedAsATrialSublot()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        TurnRuleOn(fixture);
        fixture.Catalog.Set(fixture.Demand(Demand, TrialSublot, Now.AddMinutes(-31)));
        fixture.BoxCounts.Set(TrialSublot, 4);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.True(new TrialSublotCriterion().Order < new CatalogAgeWindowCriterion().Order);
        Assert.Empty(await AcceptedAsync(fixture));
        Assert.Equal(DispatchReasonCodes.DemandTrialSublot, (await fixture.BacklogAsync(Demand)).ReasonCode);
        Assert.Empty(await fixture.Context.Set<StructuralDispatchBlockRow>().AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>
    /// 受理前重读目录时同样判：开轮时是正常批号、判据放行；重读那一刻 MES 把它改成试验批号。决策事实（SUBLOT）变了，放弃本次受理；
    /// 下一轮按新批号判，报试验批的码、仍不派。先断言重读确实发生了。
    /// </summary>
    [Fact]
    public async Task ASublotThatTurnsIntoATrialOneAtIntakesRereadIsNotAccepted()
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync("Q26102047-1");
        AcceptedDemandSnapshot ordinary = (await fixture.Catalog.ReadCatalogAsync(Token)).Items.Single();
        AcceptedDemandSnapshot trial = ordinary with
        {
            Sublot = TrialSublot,
            TransportDemandKey = $"{TrialSublot}|WIRE_TO_GATE",
            DemandRevision = ordinary.DemandRevision + 1,
        };
        fixture.BoxCounts.Set(TrialSublot, 4);
        int reads = 0;
        fixture.Catalog.BeforeRead = read =>
        {
            reads = read;
            if (read == 2)
            {
                fixture.Catalog.Set(trial);
            }
        };

        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.True(reads >= 2, $"intake never re-read the catalog (reads: {reads})");
        Assert.Empty(await AcceptedAsync(fixture));

        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Empty(await AcceptedAsync(fixture));
        Assert.Equal(DispatchReasonCodes.DemandTrialSublot, (await fixture.BacklogAsync(Demand)).ReasonCode);
    }

    /// <summary>
    /// 宿主经依赖注入装链，不经 <see cref="DispatchAdmissionCriteria.Default"/>：整轮夹具用的是后者，漏注册时它们照样绿。
    /// 日志的跨轮状态是单例，派车轮的必填参数。
    /// </summary>
    [Fact]
    public void TheHostRegistersTheCriterionAndTheSingletonLog()
    {
        ServiceCollection services = new();
        services.AddDispatchAdmission();

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IDispatchAdmissionCriterion)
            && descriptor.ImplementationType == typeof(TrialSublotCriterion));
        Assert.Equal(
            ServiceLifetime.Singleton,
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(TrialSublotFilterLog)).Lifetime);
    }

    /// <summary>在途链从空闲链派生（REQ-0196：途中追加给在途车的候选也是未受理），所以同样有这一道。</summary>
    [Fact]
    public void TheInTransitChainCarriesTheRuleToo()
    {
        IReadOnlyList<IDispatchAdmissionCriterion> inTransit = DispatchAdmissionCriteria.InTransit(
            [new TrialSublotCriterion(), new VehicleDynamicFactsCriterion(Microsoft.Extensions.Options.Options.Create(new JourneyRuntimeOptions()))],
            Microsoft.Extensions.Options.Options.Create(new JourneyRuntimeOptions()));

        Assert.Contains(inTransit, criterion => criterion is TrialSublotCriterion);
    }

    // ---- 已受理的不受影响 ----------------------------------------------------------------------------------

    /// <summary>
    /// 规则关着时受理的试验批，规则打开之后连跑几轮：旅程不被收回、需求不被取消、订单意图照旧，积压行仍记受理时刻、不报试验批的码，
    /// 也不当作离开目录。
    /// </summary>
    [Fact]
    public async Task AnAcceptedTrialSublotIsDeliveredAfterTheRuleIsTurnedOn()
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(TrialSublot, ruleOn: false);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal([Demand], await AcceptedAsync(fixture));
        JourneyRuntimeRow journey = await fixture.Context.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        int intents = await fixture.Context.OrderIntents.CountAsync(Token);

        TurnRuleOn(fixture);
        for (int round = 0; round < 4; round++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            await fixture.Engine.ExecuteOnceAsync(Token);
        }

        fixture.Context.ChangeTracker.Clear();
        JourneyRuntimeRow after = await fixture.Context.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        Assert.Equal(journey.JourneyId, after.JourneyId);
        Assert.NotEqual(JourneyRuntimeStage.Completed, after.Stage);
        AcceptedDemandRow accepted = await fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(Token);
        Assert.NotEqual(DemandExecutionStatus.Cancelled, accepted.Status);
        Assert.Equal(intents, await fixture.Context.OrderIntents.CountAsync(Token));
        JourneyBacklogRow backlog = await fixture.BacklogAsync(Demand);
        Assert.NotNull(backlog.AcceptedAt);
        Assert.NotEqual(DispatchReasonCodes.DemandTrialSublot, backlog.ReasonCode);
        Assert.NotEqual(DispatchReasonCodes.DemandLeftCatalog, backlog.ReasonCode);
    }

    /// <summary>
    /// 放回待派、仍为已受理（REQ-0328）：规则关着时受理、按释放服务的落库形状释放之后，规则打开，照样重新派车。
    /// 对照：同一轮里一条从未受理的试验批被挡下——证明规则确实开着。
    /// </summary>
    [Fact]
    public async Task ATrialSublotReleasedBackForRedispatchIsRedispatched()
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(TrialSublot, ruleOn: false);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal([Demand], await AcceptedAsync(fixture));
        await ReleaseForRedispatchAsync(fixture);
        TurnRuleOn(fixture);
        fixture.Catalog.Set(
            (await fixture.Catalog.ReadCatalogAsync(Token)).Items.Single(),
            fixture.Demand(Other, "Q26079308-1", fixture.Clock.GetUtcNow().AddMinutes(-1)));
        fixture.BoxCounts.Set("Q26079308-1", 4);

        await fixture.Engine.ExecuteOnceAsync(Token);

        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(2, await fixture.Context.JourneyRuntimes.CountAsync(Token));
        Assert.False(await DemandJourneyLookup.ReleasedForRedispatch(fixture.Context).AnyAsync(Token));
        Assert.NotEqual(DispatchReasonCodes.DemandTrialSublot, (await fixture.BacklogAsync(Demand)).ReasonCode);
        Assert.Equal([Demand], await AcceptedAsync(fixture));
        Assert.Equal(DispatchReasonCodes.DemandTrialSublot, (await fixture.BacklogAsync(Other)).ReasonCode);
    }

    // ---- 不告警 ----------------------------------------------------------------------------------------------

    /// <summary>试验批越过防饥饿阈值（60 秒）也不升级，连跑三轮：防饥饿标记与告警日志都没有。</summary>
    [Fact]
    public async Task ATrialSublotIsNeverEscalatedForStarvation()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        TurnRuleOn(fixture);
        await fixture.ImportStarvationThresholdsAsync((Zone, 60));
        fixture.Catalog.Set(fixture.Demand(Demand, TrialSublot, Now.AddMinutes(-5)));

        for (int round = 0; round < 3; round++)
        {
            await fixture.Engine.ExecuteOnceAsync(Token);
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        JourneyBacklogRow backlog = await fixture.BacklogAsync(Demand);
        Assert.Equal(DispatchReasonCodes.DemandTrialSublot, backlog.ReasonCode);
        Assert.Null(backlog.StarvationEscalatedAt);
        Assert.DoesNotContain(fixture.StarvationLog.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains(Demand, StringComparison.Ordinal));
    }

    /// <summary>
    /// 不立结构性阻断：AREA N22-1 在分区归属表里、地图上没有站点，正常批号会立 <c>AREA_STATION_NOT_FOUND</c>；试验批停在判据链前段，什么都不立。
    /// </summary>
    [Fact]
    public async Task ATrialSublotRaisesNoStructuralBlock()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        TurnRuleOn(fixture);
        fixture.Catalog.Set(fixture.Demand(Demand, TrialSublot, Now.AddMinutes(-1), "N22-1"));
        fixture.BoxCounts.Set(TrialSublot, 4);

        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Empty(await fixture.Context.Set<StructuralDispatchBlockRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(DispatchReasonCodes.DemandTrialSublot, (await fixture.BacklogAsync(Demand)).ReasonCode);
    }

    /// <summary>
    /// 规则关着时立过的结构性阻断，规则打开之后清掉：那条「请人工搬运」的阻断不再成立，条文不给试验批挂人工搬运告警。
    /// </summary>
    [Fact]
    public async Task ABlockRaisedBeforeTheRuleWasOnIsClearedOnceItIs()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(Demand, TrialSublot, Now.AddMinutes(-1), "N22-1"));
        fixture.BoxCounts.Set(TrialSublot, 4);
        await fixture.Engine.ExecuteOnceAsync(Token);
        StructuralDispatchBlockRow raised = Assert.Single(
            await fixture.Context.Set<StructuralDispatchBlockRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Equal("AREA_STATION_NOT_FOUND", raised.ReasonCode);
        Assert.Null(raised.ClearedAt);

        TurnRuleOn(fixture);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Engine.ExecuteOnceAsync(Token);

        fixture.Context.ChangeTracker.Clear();
        Assert.All(
            await fixture.Context.Set<StructuralDispatchBlockRow>().AsNoTracking().ToArrayAsync(Token),
            block => Assert.NotNull(block.ClearedAt));
        Assert.Equal(DispatchReasonCodes.DemandTrialSublot, (await fixture.BacklogAsync(Demand)).ReasonCode);
    }

    // ---- AREA→EQP 唯一性（用户 10-21 定：忽略试验批） ---------------------------------------------------------------

    /// <summary>
    /// 未受理的试验批不参加 AREA→EQP 唯一性判断：试验批机台与正常需求的机台同 AREA、不同 EQP（现场 N8-4：2ZPB28 与 3QHS3016），
    /// 正常需求照常派车，不被 <c>AREA_EQP_NOT_UNIQUE</c> 挡住。
    /// </summary>
    [Fact]
    public async Task ATrialRowDoesNotMakeAnOrdinaryDemandsAreaAmbiguous()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        TurnRuleOn(fixture);
        fixture.Catalog.Set(
            fixture.Demand(Demand, TrialSublot, Now.AddMinutes(-2), "N1-1", "2ZPB28"),
            fixture.Demand(Other, "Q26102047-1", Now.AddMinutes(-1), "N1-1", "3QHS3016"));
        fixture.BoxCounts.Set(TrialSublot, 4);
        fixture.BoxCounts.Set("Q26102047-1", 4);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal([Other], await AcceptedAsync(fixture));
        Assert.Equal(DispatchReasonCodes.DemandTrialSublot, (await fixture.BacklogAsync(Demand)).ReasonCode);
    }

    /// <summary>
    /// 已受理的试验批照样参加：它不受本判定约束，它的 EQP 仍是有效观测。规则关着时受理的 A（EQP-01），规则打开之后仍在目录里；
    /// 同 AREA、不同 EQP 的正常需求 B 照旧报 <c>AREA_EQP_NOT_UNIQUE</c>。
    /// </summary>
    [Fact]
    public async Task AnAcceptedTrialRowStillTakesPartInTheAreaCheck()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot accepted = fixture.Demand(Demand, TrialSublot, Now.AddMinutes(-1), "N1-1", "EQP-01");
        fixture.Catalog.Set(accepted);
        fixture.BoxCounts.Set(TrialSublot, 4);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal([Demand], await AcceptedAsync(fixture));

        TurnRuleOn(fixture);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Catalog.Set(accepted, fixture.Demand(Other, "Q26102047-1", fixture.Clock.GetUtcNow().AddMinutes(-1), "N1-1", "EQP-02"));
        fixture.BoxCounts.Set("Q26102047-1", 4);
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal("AREA_EQP_NOT_UNIQUE", (await fixture.BacklogAsync(Other)).ReasonCode);
    }

    // ---- 日志 ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 每轮滤掉几条、哪几个批号，只在变化时记：两轮不变只记一次；多一条试验批再记一次；试验批都离开目录后记一次「0 条」。
    /// </summary>
    [Fact]
    public async Task TheFilteredSublotsAreLoggedOnlyWhenTheyChange()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        TurnRuleOn(fixture);
        AcceptedDemandSnapshot first = fixture.Demand(Demand, TrialSublot, Now.AddMinutes(-1));
        fixture.Catalog.Set(first);

        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Single(FilterLines(fixture));
        Assert.Contains(TrialSublot, FilterLines(fixture)[0], StringComparison.Ordinal);

        fixture.Catalog.Set(first, fixture.Demand(Other, "Q26079308-1", fixture.Clock.GetUtcNow().AddMinutes(-1)));
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(2, FilterLines(fixture).Length);
        Assert.Contains("Q26079308-1", FilterLines(fixture)[1], StringComparison.Ordinal);
        Assert.Contains(TrialSublot, FilterLines(fixture)[1], StringComparison.Ordinal);

        fixture.Catalog.Set();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(3, FilterLines(fixture).Length);
        Assert.Contains(" 0 ", FilterLines(fixture)[2], StringComparison.Ordinal);
    }

    /// <summary>
    /// 规则关着时受理的试验批，规则打开后不算「滤掉」（审查 L1）：日志只列那条从未受理的试验批，不列已受理的那条。
    /// 取红：日志集合不排除已受理的（变异 N3），已受理的批号也出现在日志里。
    /// </summary>
    [Fact]
    public async Task AnAcceptedTrialSublotIsNotLoggedAsFiltered()
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(TrialSublot, ruleOn: false);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal([Demand], await AcceptedAsync(fixture));
        Assert.Empty(FilterLines(fixture));

        TurnRuleOn(fixture);
        fixture.Catalog.Set(
            (await fixture.Catalog.ReadCatalogAsync(Token)).Items.Single(),
            fixture.Demand(Other, "Q26079308-1", fixture.Clock.GetUtcNow().AddMinutes(-1)));
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Engine.ExecuteOnceAsync(Token);

        string line = Assert.Single(FilterLines(fixture));
        Assert.Contains("Q26079308-1", line, StringComparison.Ordinal);
        Assert.DoesNotContain(TrialSublot, line, StringComparison.Ordinal);
        Assert.Contains(" 1 ", line, StringComparison.Ordinal);
    }

    // ---- 看板与分类 ------------------------------------------------------------------------------------------

    /// <summary>
    /// 看板积压列出试验批，带原因码与中文说明；不算静默、归普通积压，不立结构性阻断。
    /// </summary>
    [Fact]
    public async Task TheDashboardListsTheRefusalWithAChineseDescription()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Context.JourneyBacklog.Add(new JourneyBacklogRow
        {
            DemandId = Demand,
            TransportDemandKey = $"{TrialSublot}|WIRE_TO_GATE",
            FirstSeenAt = Now.AddMinutes(-4),
            DemandCreatedAt = Now.AddMinutes(-5),
            DecisionFingerprint = "fingerprint",
            ReasonCode = DispatchReasonCodes.DemandTrialSublot,
            LastSeenAt = Now,
        });
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        using JsonDocument fact = JsonDocument.Parse(JsonSerializer.Serialize(
            await new DispatchBacklogQueryEndpoint().ReadAsync(fixture.Context, Token)));

        Assert.True(DispatchBacklogQueryEndpoint.Descriptions.TryGetValue(DispatchReasonCodes.DemandTrialSublot, out string? description));
        Assert.Matches(@"\p{IsCJKUnifiedIdeographs}", description);
        Assert.Contains("试验", description, StringComparison.Ordinal);
        JsonElement row = Assert.Single(fact.RootElement.GetProperty("backlog").EnumerateArray());
        Assert.Equal(DispatchReasonCodes.DemandTrialSublot, row.GetProperty("reasonCode").GetString());
        Assert.Equal(description, row.GetProperty("reasonDescription").GetString());
        Assert.Equal(0, fact.RootElement.GetProperty("silentBacklogCount").GetInt32());
        Assert.Equal(DispatchReasonClass.Backlog, StructuralDispatchClassification.ClassOf(DispatchReasonCodes.DemandTrialSublot));
        Assert.False(DispatchReasonCodes.IsSilent(DispatchReasonCodes.DemandTrialSublot));
    }

    // ---- 配置 ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 出厂为空（关）；字符位不得为负；字符位与取值要么都写、要么都空，只写一半拒绝启动（半条规则等于没有，却看着像开了）。
    /// 出厂配置写明这两项。
    /// </summary>
    [Fact]
    public void TheRuleShipsEmptyAndHalfARuleIsRefused()
    {
        Assert.Equal(0, new JourneyRuntimeOptions().TrialSublotCharacterPosition);
        Assert.Equal("", new JourneyRuntimeOptions().TrialSublotCharacters);
        JourneyRuntimeOptionsValidator validator = new(new ConfigurationBuilder().Build());
        foreach ((int position, string characters) in new[] { (0, ""), (6, "9"), (1, "Q"), (6, "89") })
        {
            JourneyRuntimeOptions options = JourneyRuntimeOptionsTests.ValidEnabledOptions();
            options.TrialSublotCharacterPosition = position;
            options.TrialSublotCharacters = characters;
            Assert.DoesNotContain(validator.Validate(null, options).Failures ?? [], failure => failure.Contains("TrialSublot", StringComparison.Ordinal));
        }
        // 审查 L4：取值里有空白字符的拒绝——" 9" 会把第 6 位是空格的批号也滤掉，读回结果行也带不了空格。
        foreach ((int position, string characters) in new[] { (6, ""), (0, "9"), (-1, "9"), (-1, ""), (6, " 9"), (6, "9\t"), (6, "　") })
        {
            JourneyRuntimeOptions options = JourneyRuntimeOptionsTests.ValidEnabledOptions();
            options.TrialSublotCharacterPosition = position;
            options.TrialSublotCharacters = characters;
            Assert.Contains(validator.Validate(null, options).Failures ?? [], failure => failure.Contains("TrialSublot", StringComparison.Ordinal));
        }

        IConfiguration shipped = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), "src", "ControlServer.Host", "appsettings.json"), optional: false)
            .Build();
        Assert.Equal("0", shipped["JourneyRuntime:trialSublotCharacterPosition"]);
        Assert.Equal("", shipped["JourneyRuntime:trialSublotCharacters"]);
    }

    /// <summary>两项都从配置绑定（实例定义写进 appsettings.Production.json 的就是这两个键）。</summary>
    [Fact]
    public void TheRuleBindsFromConfiguration()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JourneyRuntime:trialSublotCharacterPosition"] = "6",
                ["JourneyRuntime:trialSublotCharacters"] = "9",
            })
            .Build();

        JourneyRuntimeOptions options = configuration.GetSection("JourneyRuntime").Get<JourneyRuntimeOptions>()!;

        Assert.True(TrialSublotRule.From(options).Matches(TrialSublot));
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------

    private static TrialSublotRule ProductionRule() => TrialSublotRule.From(new JourneyRuntimeOptions
    {
        TrialSublotCharacterPosition = 6,
        TrialSublotCharacters = "9",
    });

    private static void TurnRuleOn(RuntimeFixture fixture)
    {
        fixture.Options.TrialSublotCharacterPosition = 6;
        fixture.Options.TrialSublotCharacters = "9";
    }

    private static async Task<RuntimeFixture> FixtureWithDemandAsync(string sublot, bool ruleOn = true)
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        if (ruleOn)
        {
            TurnRuleOn(fixture);
        }
        fixture.Catalog.Set(fixture.Demand(Demand, sublot, fixture.Clock.GetUtcNow().AddMinutes(-1)));
        fixture.BoxCounts.Set(sublot, 4);
        return fixture;
    }

    private static string[] FilterLines(RuntimeFixture fixture) =>
        [.. fixture.EngineLog.Entries
            .Where(entry => entry.Message.StartsWith("Trial sublot rule", StringComparison.Ordinal))
            .Select(entry => entry.Message)];

    private static async Task<string[]> AcceptedAsync(RuntimeFixture fixture) =>
        await fixture.Context.AcceptedDemands.AsNoTracking().Select(row => row.DemandId).ToArrayAsync(Token);

    /// <summary>释放的落库形状，同 <c>CatalogAgeWindowTests.ReleaseForRedispatchAsync</c>。</summary>
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
