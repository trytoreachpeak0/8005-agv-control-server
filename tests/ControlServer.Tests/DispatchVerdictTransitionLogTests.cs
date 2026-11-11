using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 派车判定变化日志（control-server#884）：每对（车, 需求）的判定码一变（含第一次判到、判定消失）记一条 2884，不变不记；
/// 需求离开积压就忘掉它；跟踪有上限，满了记一次 2885。
/// </summary>
/// <remarks>
/// 整轮跑：引擎、派车轮、轮末汇总、SQLite（<see cref="RuntimeFixture"/>，一辆空闲车）。让码变的手段是试验机批号规则（control-server#828）：
/// 开着时这条需求被判 <c>DEMAND_TRIAL_SUBLOT</c>，关掉之后同一条需求合格、被接走——规则是一轮读一次的配置，开关它不碰别的判据。
/// </remarks>
public sealed class DispatchVerdictTransitionLogTests
{
    private const string Demand = "10000000-0000-4000-8000-000000000884";
    private const string Other = "10000000-0000-4000-8000-000000000885";
    private const string TrialSublot = "Q26079458-1";
    private const string OtherTrialSublot = "Q26079308-1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 票面第 3 条：第一轮第一次判到记一条；第二轮码不变，零条；第三轮码变了（规则关掉、需求合格被接走）正好一条；第四轮零条。
    /// 每条带车、需求号、新旧码、轮次与车的几样输入（会话代次、电量、站点）和需求的区、任务类型。
    /// </summary>
    [Fact]
    public async Task OneLinePerChangeAndNoneForAnUnchangedRound()
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(TrialSublot);

        await RoundAsync(fixture);
        string first = Assert.Single(VerdictLines(fixture));
        Assert.Contains($"vehicle {fixture.Options.AgvId} on demand {Demand}", first, StringComparison.Ordinal);
        Assert.Contains($"from NONE to {DispatchReasonCodes.DemandTrialSublot} (round 1 at ", first, StringComparison.Ordinal);
        Assert.Matches(@"\(round 1 at \d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}\+00:00\);", first);
        Assert.Matches(@"session generation \d+, battery 80% NO_CHARGE, station 1, area N1-1, task type WIRE_TO_GATE\.$", first);

        await RoundAsync(fixture);
        Assert.Single(VerdictLines(fixture));

        TurnRuleOff(fixture);
        await RoundAsync(fixture);
        Assert.Equal([Demand], await AcceptedAsync(fixture));
        string[] lines = VerdictLines(fixture);
        Assert.Equal(2, lines.Length);
        Assert.Contains(
            $"from {DispatchReasonCodes.DemandTrialSublot} to {DispatchAdmissionChain.Eligible} (round 3 at ",
            lines[1],
            StringComparison.Ordinal);
        Assert.Equal(0, fixture.VerdictTransitions.TrackedPairCount);

        await RoundAsync(fixture);
        Assert.Equal(2, VerdictLines(fixture).Length);
    }

    /// <summary>
    /// 需求离开目录（没被接走）：下一轮记一条「判定消失」（新码 NONE，区与任务类型取最后一次判它时的），然后忘掉它，再下一轮零条。
    /// </summary>
    [Fact]
    public async Task ADemandThatLeavesTheCatalogIsLoggedAsClearedOnceAndForgotten()
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(TrialSublot);
        await RoundAsync(fixture);
        Assert.Equal(1, fixture.VerdictTransitions.TrackedPairCount);

        fixture.Catalog.Set();
        await RoundAsync(fixture);
        string[] lines = VerdictLines(fixture);
        Assert.Equal(2, lines.Length);
        Assert.Contains(
            $"on demand {Demand} changed from {DispatchReasonCodes.DemandTrialSublot} to NONE (round 2 at ",
            lines[1],
            StringComparison.Ordinal);
        Assert.Contains("area N1-1, task type WIRE_TO_GATE.", lines[1], StringComparison.Ordinal);
        Assert.Equal(0, fixture.VerdictTransitions.TrackedPairCount);

        await RoundAsync(fixture);
        Assert.Equal(2, VerdictLines(fixture).Length);
    }

    /// <summary>
    /// 更早就受理了的需求不跟踪：它每轮仍被判成「已受理」，跟踪它只会在重启（换一份新的跨轮状态）后多出一批「首次判到」。
    /// </summary>
    [Fact]
    public async Task AnAlreadyAcceptedDemandIsNotTrackedAfterARestart()
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(TrialSublot, ruleOn: false);
        await RoundAsync(fixture);
        Assert.Equal([Demand], await AcceptedAsync(fixture));
        int before = VerdictLines(fixture).Length;

        fixture.VerdictTransitions = new DispatchVerdictTransitionLog();
        await fixture.RecreateEngineAsync();
        await RoundAsync(fixture);

        Assert.Equal(before, VerdictLines(fixture).Length);
        Assert.Equal(0, fixture.VerdictTransitions.TrackedPairCount);
    }

    /// <summary>
    /// 上限：满了之后新出现的对不跟踪、不记，从未满变满的那一轮记一次警告（2885），一直满着不再记；已跟踪的那一对照常。
    /// </summary>
    [Fact]
    public async Task TrackingIsBoundedAndSaysSoOnce()
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(TrialSublot);
        fixture.VerdictTransitions = new DispatchVerdictTransitionLog(capacity: 1);
        await fixture.RecreateEngineAsync();
        fixture.Catalog.Set(
            fixture.Demand(Demand, TrialSublot, fixture.Clock.GetUtcNow().AddMinutes(-2)),
            fixture.Demand(Other, OtherTrialSublot, fixture.Clock.GetUtcNow().AddMinutes(-1)));

        await RoundAsync(fixture);
        await RoundAsync(fixture);

        Assert.Single(VerdictLines(fixture));
        Assert.Equal(1, fixture.VerdictTransitions.TrackedPairCount);
        string full = Assert.Single(fixture.EngineLog.Entries, entry =>
            entry.Message.StartsWith("Dispatch verdict change tracking is full", StringComparison.Ordinal)).Message;
        Assert.Contains("(1 vehicle/demand pairs); 1 pair(s)", full, StringComparison.Ordinal);
    }

    /// <summary>
    /// 车这一轮没参加（读 RIoT 抛异常，同在途车报位置 0、计划读不出时整段不参加）：它的码保持不动，那一轮与下一轮都零条；
    /// 之后码真变了，记的那一条带的是那一轮的轮次号——不参加的几轮也走过了比对、计过轮次（审查 M1，变异 U1）。
    /// </summary>
    [Fact]
    public async Task AVehicleThatSitsOutARoundKeepsItsCodesAndLogsNothing()
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(TrialSublot);
        await RoundAsync(fixture);
        Assert.Single(VerdictLines(fixture));

        fixture.Riot.ReadVehicleDelay = _ => Task.FromException(new HttpRequestException("RIoT unreachable"));
        await RoundAsync(fixture);
        await RoundAsync(fixture);
        Assert.Single(VerdictLines(fixture));
        Assert.Equal(1, fixture.VerdictTransitions.TrackedPairCount);

        fixture.Riot.ReadVehicleDelay = null;
        TurnRuleOff(fixture);
        await RoundAsync(fixture);
        string[] lines = VerdictLines(fixture);
        Assert.Equal(2, lines.Length);
        Assert.Contains(
            $"from {DispatchReasonCodes.DemandTrialSublot} to {DispatchAdmissionChain.Eligible} (round 4 at ",
            lines[1],
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 需求离开目录的那一轮车正好没参加：照样记一条「判定消失」并忘掉它，不因为「这辆车说不出来」而永远留着（审查 M1，变异 U5）。
    /// </summary>
    [Fact]
    public async Task ADemandThatLeavesTheCatalogWhileTheVehicleSitsOutIsStillForgotten()
    {
        await using RuntimeFixture fixture = await FixtureWithDemandAsync(TrialSublot);
        await RoundAsync(fixture);
        Assert.Single(VerdictLines(fixture));

        fixture.Riot.ReadVehicleDelay = _ => Task.FromException(new HttpRequestException("RIoT unreachable"));
        fixture.Catalog.Set();
        await RoundAsync(fixture);

        string[] lines = VerdictLines(fixture);
        Assert.Equal(2, lines.Length);
        Assert.Contains(
            $"on demand {Demand} changed from {DispatchReasonCodes.DemandTrialSublot} to NONE (round 2 at ",
            lines[1],
            StringComparison.Ordinal);
        Assert.Equal(0, fixture.VerdictTransitions.TrackedPairCount);
    }

    /// <summary>
    /// 受理被拒的认领（control-server#242）仍在等：它在 <c>AcceptedDemandIds</c> 里，但也在 <c>ClaimsIntakeRefused</c> 里，不当成离开积压。
    /// 拒收那一轮记它变成 <c>ELIGIBLE</c>、照旧跟踪；下一轮码不变，零条——忘掉它的话下一轮会再记一次「首次判到」（审查 S1，变异 U2）。
    /// 直接喂轮次结局：整轮夹具里造一次受理拒收要动受理本身，与这里要钉的口径无关。
    /// </summary>
    [Fact]
    public void ARefusedIntakeClaimIsStillWaiting()
    {
        DispatchVerdictTransitionLog log = new();
        DateTimeOffset now = Now;

        Assert.Single(log.Record(Outcome(UnitRound(now, accepted: []), DispatchReasonCodes.DemandTrialSublot)).Transitions);

        DispatchRoundFacts refused = UnitRound(now.AddSeconds(10), accepted: [Demand]) with
        {
            ClaimsIntakeRefused = new HashSet<string>(StringComparer.Ordinal) { Demand },
        };
        DispatchVerdictTransition changed = Assert.Single(log.Record(Outcome(refused, DispatchAdmissionChain.Eligible)).Transitions);
        Assert.Equal(DispatchAdmissionChain.Eligible, changed.NewReasonCode);
        Assert.Equal(1, log.TrackedPairCount);

        Assert.Empty(log.Record(Outcome(UnitRound(now.AddSeconds(20), accepted: []), DispatchAdmissionChain.Eligible)).Transitions);
        Assert.Equal(1, log.TrackedPairCount);
    }

    /// <summary>宿主经依赖注入拿到它：单例（跨轮状态），派车轮的必填参数——整轮夹具不经注入，漏注册时它们照样绿。</summary>
    [Fact]
    public void TheHostRegistersItAsASingleton()
    {
        ServiceCollection services = new();
        services.AddDispatchAdmission();

        Assert.Equal(
            ServiceLifetime.Singleton,
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(DispatchVerdictTransitionLog)).Lifetime);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------

    private static async Task RoundAsync(RuntimeFixture fixture)
    {
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
    }

    private static async Task<RuntimeFixture> FixtureWithDemandAsync(string sublot, bool ruleOn = true)
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        if (ruleOn)
        {
            fixture.Options.TrialSublotCharacterPosition = 6;
            fixture.Options.TrialSublotCharacters = "9";
        }
        fixture.Catalog.Set(fixture.Demand(Demand, sublot, fixture.Clock.GetUtcNow().AddMinutes(-1)));
        fixture.BoxCounts.Set(sublot, 4);
        fixture.BoxCounts.Set(OtherTrialSublot, 4);
        return fixture;
    }

    private static void TurnRuleOff(RuntimeFixture fixture)
    {
        fixture.Options.TrialSublotCharacterPosition = 0;
        fixture.Options.TrialSublotCharacters = "";
    }

    private static string[] VerdictLines(RuntimeFixture fixture) =>
        [.. fixture.EngineLog.Entries
            .Where(entry => entry.Message.StartsWith("Dispatch verdict for vehicle", StringComparison.Ordinal))
            .Select(entry => entry.Message)];

    private const string UnitAgv = "AGV-UNIT";

    private static DispatchRoundFacts UnitRound(DateTimeOffset now, string[] accepted) => new(
        new DemandCatalogSnapshot("11111111-1111-4111-8111-111111111111", 21, [UnitDemand(now)]),
        new RiotMapStationCatalogSnapshot(25, now, new string('c', 64), []),
        new SingleStationView(new RiotMapStation(210, "关卡")),
        accepted.ToHashSet(StringComparer.Ordinal),
        now,
        new VehicleDispatchPolicy([], new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), "TEST-POLICY"));

    private static AcceptedDemandSnapshot UnitDemand(DateTimeOffset now) => new(
        Demand,
        $"{TrialSublot}|WIRE_TO_GATE",
        7,
        "11111111-1111-4111-8111-111111111111",
        21,
        now,
        "SERIES-1",
        "WIRE_TO_GATE",
        TrialSublot,
        1,
        now.AddMinutes(-10),
        now.AddMinutes(-9),
        "TRACE-1",
        "COMMIT-1",
        new LiveMesFieldSet("N1-1", "EQP-01", "STEP-01", now, "PDFN5×6-8L(12R)"));

    private static DispatchRoundOutcome Outcome(DispatchRoundFacts round, string reasonCode) => new(
        round,
        [new DispatchVehicleOutcome(UnitAgv, $"KEY-{UnitAgv}",
        [
            new DispatchCandidateVerdict(
                new DispatchCandidateEvaluation(
                    round.Catalog.Items[0],
                    round,
                    new DispatchVehicleFacts(
                        $"KEY-{UnitAgv}",
                        UnitAgv,
                        new OnboardDispatchFacts(1, [1, 2, 3, 4], true, true, true, true, false),
                        new RiotVehicleObservation($"KEY-{UnitAgv}", true, true, "IDLE", "MAP-25", 4, 90, "NO_CHARGE", 0, round.Now),
                        round.Now)),
                reasonCode),
        ])]);

    private static async Task<string[]> AcceptedAsync(RuntimeFixture fixture) =>
        await fixture.Context.AcceptedDemands.AsNoTracking().Select(row => row.DemandId).ToArrayAsync(Token);
}
