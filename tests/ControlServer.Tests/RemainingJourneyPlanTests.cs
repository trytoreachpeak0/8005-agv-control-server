using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Tests;

/// <summary>
/// 旅程计划只下发剩余停靠，途中追加的 9 段门禁只数剩余停靠，整趟停靠总数另有可配上限（control-server#749，派车问题 1；
/// REQ-0196 经 CP-0015 修订，需求基线 v1.14.0；协议 wire notes「remaining plan」、向量 <c>DISPLAY_REMAINING_JOURNEY_PLAN</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 现场（10-17，agv02 在 217）：一趟走过 5 站、还剩 3 站的旅程，门禁按整趟 5 + 3 + 2 &gt; 9 拒绝追加，车少接活。
/// 用户定：只发剩余站，车载屏不再显示走过的站，整趟停靠上限默认 20、可配。
/// </para>
/// <para>
/// 规划器的用例沿用 <see cref="Batch7EnRouteAppendPlannerTests"/> 的一维站点图：站号就是坐标，两站之间代价是站号之差乘一千毫米，
/// 上限给得足够大，所以这里每条拒绝都只可能来自腿数或停靠总数。
/// </para>
/// <para>
/// 清桩约束（调度在票上补的，hmi#362 发现）：车载端人工清桩入口从计划里的 <c>CHARGER</c> 腿认桩号，不看腿状态。那张计划
/// （原桩腿 <c>COMPLETED</c> + 等待点腿 <c>ACTIVE</c>）由 <c>JourneyRuntimeEngine.ClearanceMove</c> 直接拼两条腿发出，不经
/// <see cref="JourneyPlanBuilder.Plan"/>，过滤只放在 <c>Plan</c> 里所以碰不到它；端到端的钉子是
/// <c>ChargingClearanceToWaitingPointTests</c> 那条断言 <c>CHARGER:COMPLETED, WAITING_POINT:ACTIVE</c> 的用例，这里另钉一条
/// 「线上那一份照样带着已完成的桩腿」。
/// </para>
/// </remarks>
[Trait("IntegrationSlice", "FP-IS-08")]
public sealed class RemainingJourneyPlanTests
{
    private const string ZoneA = "ZONE-A";

    private static readonly Func<int, int, long?> Distance = (from, to) => Math.Abs(from - to) * 1000L;

    private static readonly DateTimeOffset StagedAt = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 途中追加：9 段只数剩余 -----------------------------------------------------------------

    /// <summary>
    /// 现场那一格：走过 5 站、还剩 3 站，再追加一条（两站）——剩余 5 段，受理。整趟数是 5 + 3 + 2 = 10，旧门禁在这里拒。
    /// </summary>
    [Fact]
    public void AnAppendAfterFivePassedStopsIsPlacedWhenTheStopsAheadFit()
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanFrom(currentNextStopIndex: 5, PickupChain(8)),
            Candidate(),
            Zones(),
            Distance);

        Assert.Null(decision.RefusalReasonCode);
        // 序位仍覆盖整趟：走过的 5 个原样占住 1..5，新需求落在它们之后。
        Assert.Equal(10, decision.Placement!.Resequenced.Count);
        Assert.True(decision.Placement.PickupSequence > 5);
    }

    /// <summary>
    /// 剩余停靠加新插入正好 9 段放行、10 段拒——前面走过多少站都不影响。两行只差剩余的那一站，9 这个数因此上下都被钉住。
    /// </summary>
    [Theory]
    [InlineData(10, null)]
    [InlineData(11, DispatchReasonCodes.EnRouteAppendPlanLimitReached)]
    public void TheNineLegLimitCountsOnlyTheStopsAhead(int stops, string? expectedRefusal)
    {
        // 走过 3 站：剩 7（或 8）站，加新需求两站 = 9（或 10）。整趟 12（或 13），远在 20 之内。
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanFrom(currentNextStopIndex: 3, PickupChain(stops)),
            Candidate(),
            Zones(),
            Distance);

        Assert.Equal(expectedRefusal, decision.RefusalReasonCode);
    }

    // ---- 整趟停靠总数上限 --------------------------------------------------------------------------

    /// <summary>
    /// 整趟（含走过的）恰好 20 放行、21 拒，拒的原因码是整趟上限那一个，不是 9 段——两行剩余都只有 6 段。
    /// </summary>
    [Theory]
    [InlineData(18, 14, null)]
    [InlineData(19, 15, DispatchReasonCodes.EnRouteAppendJourneyStopLimitReached)]
    public void TheWholeJourneyMayHaveTwentyStopsByDefault(int stops, int passed, string? expectedRefusal)
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanFrom(currentNextStopIndex: passed, PickupChain(stops)),
            Candidate(),
            Zones(),
            Distance);

        Assert.Equal(expectedRefusal, decision.RefusalReasonCode);
    }

    /// <summary>上限是可配的：改成 12，同一趟（走过 5、剩 6，追加后 13）就被拒；改成 13 又放行。</summary>
    [Theory]
    [InlineData(13, null)]
    [InlineData(12, DispatchReasonCodes.EnRouteAppendJourneyStopLimitReached)]
    public void TheJourneyStopLimitFollowsTheConfiguredValue(int maximumJourneyStops, string? expectedRefusal)
    {
        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            PlanFrom(currentNextStopIndex: 5, PickupChain(11)),
            Candidate(),
            Zones(),
            Distance,
            maximumJourneyStops);

        Assert.Equal(expectedRefusal, decision.RefusalReasonCode);
    }

    /// <summary>整趟上限挡住的车在待送点上算「装不下」（与 9 段同一类），否则车会在待送点白等到持货期限。</summary>
    [Fact]
    public void TheJourneyStopLimitIsACapacityRefusalAtTheStagingStation() =>
        Assert.True(StagingDepartureRule.IsCapacityRefusal(DispatchReasonCodes.EnRouteAppendJourneyStopLimitReached));

    /// <summary>配置项默认 20，校验不许小于 9（一趟旅程至少要装得下一张满的计划），9 本身可以。</summary>
    [Fact]
    public void MaximumJourneyStopsDefaultsToTwentyAndIsNeverBelowNine()
    {
        const string failure = "MaximumJourneyStops must be at least 9.";
        Assert.Equal(20, new JourneyRuntimeOptions().MaximumJourneyStops);
        JourneyRuntimeOptionsValidator validator = new(new ConfigurationBuilder().Build());
        JourneyRuntimeOptions options = JourneyRuntimeOptionsTests.ValidEnabledOptions();

        foreach (int accepted in new[] { 9, 20, 100 })
        {
            options.MaximumJourneyStops = accepted;
            Assert.DoesNotContain(failure, validator.Validate(null, options).Failures ?? [], StringComparer.Ordinal);
        }
        foreach (int refused in new[] { 8, 0, -1 })
        {
            options.MaximumJourneyStops = refused;
            Assert.Contains(failure, validator.Validate(null, options).Failures ?? [], StringComparer.Ordinal);
        }
    }

    // ---- 投影：只下发剩余停靠 ----------------------------------------------------------------------

    /// <summary>
    /// 走过的停靠不投影；当前停靠那条腿在，按车到没到是 <c>ARRIVED</c> 或 <c>ACTIVE</c>；其后是 <c>PLANNED</c>。
    /// </summary>
    [Theory]
    [InlineData(true, "ARRIVED")]
    [InlineData(false, "ACTIVE")]
    public void PassedStopsAreNotProjectedAndTheCurrentStopIs(bool arrived, string currentState)
    {
        JourneyRuntimeRow runtime = Batch7PlanRevisionTests.PlanRuntime();
        JourneyStopRow first = Batch7PlanRevisionTests.StopRow("s-1", 1, JourneyStopRoles.Pickup, JourneyStopStatuses.Completed);
        JourneyStopRow second = Batch7PlanRevisionTests.StopRow("s-2", 2, JourneyStopRoles.Pickup, JourneyStopStatuses.Completed);
        JourneyStopRow current = Batch7PlanRevisionTests.StopRow("s-3", 3, JourneyStopRoles.Pickup, JourneyStopStatuses.Active);
        JourneyStopRow unload = Batch7PlanRevisionTests.StopRow("s-4", 4, JourneyStopRoles.Unload, JourneyStopStatuses.Pending);

        UpcomingStopPlanProjection plan = JourneyPlanBuilder.Plan(
            runtime, [first, second, current, unload], current, arrived, revision: 9);

        Assert.Equal(
            [("leg-s-3", currentState), ("leg-s-4", "PLANNED")],
            plan.Legs.Select(leg => (leg.MovementLegId, leg.State)));
    }

    /// <summary>
    /// 持货等单时同站追加出来的第二个停靠（现场 agv02 在 217 的 e6989792 就是这一形态）：第一个停靠在车「离开」它去第二个时已
    /// <c>COMPLETED</c>，车物理上没动。车所在站那条腿仍在——是第二个停靠那条，带着 <c>WIRE_STAGING</c>，车载端按站号找
    /// <c>publicStationFunction</c>（hmi <c>StationFunctionOf</c>）照样找得到。
    /// </summary>
    [Fact]
    public void TheLegAtTheStationTheVehicleStandsAtStaysWhenAnEarlierStopThereIsCompleted()
    {
        JourneyRuntimeRow runtime = Batch7PlanRevisionTests.PlanRuntime();
        JourneyStopRow first = Batch7PlanRevisionTests.StopRow("s-1", 1, JourneyStopRoles.Pickup, JourneyStopStatuses.Completed);
        JourneyStopRow second = Batch7PlanRevisionTests.StopRow("s-2", 2, JourneyStopRoles.Pickup, JourneyStopStatuses.Active);
        JourneyStopRow unload = Batch7PlanRevisionTests.StopRow("s-3", 3, JourneyStopRoles.Unload, JourneyStopStatuses.Pending);
        first.StationId = second.StationId = "派工待送取货";

        UpcomingStopPlanProjection plan = JourneyPlanBuilder.Plan(
            runtime, [first, second, unload], second, arrivedAtCurrent: true, revision: 9,
            isStagingPickup: stop => stop.StationId == "派工待送取货");

        UpcomingMovementLeg atStation = Assert.Single(plan.Legs, leg => leg.StationId == "派工待送取货");
        Assert.Equal(("leg-s-2", "ARRIVED", JourneyPlanBuilder.WireStagingStationFunction),
            (atStation.MovementLegId, atStation.State, atStation.PublicStationFunction));
        Assert.DoesNotContain(plan.Legs, leg => leg.State == "COMPLETED");
    }

    /// <summary>
    /// 推进一站之后，车上那一版（还带着走过的那条腿）与此刻应发的那一份判为不同、要重发；重发的那一份发出去，再判就相同——
    /// 判定与下发读的是同一个投影。线上序号从 1 起，当前停靠恒为第 1 条。
    /// </summary>
    [Fact]
    public async Task AdvancingOneStopMakesTheReSendCheckSendExactlyTheRemainingLegs()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(Token);
        await using ControlServerDbContext context = await ContextAsync(connection);
        JourneyRuntimeRow runtime = Batch7PlanRevisionTests.PlanRuntime();
        runtime.DemandId = "00000000-0000-4000-8000-000000074900";
        JourneyStopRow first = Batch7PlanRevisionTests.StopRow("s-1", 1, JourneyStopRoles.Pickup, JourneyStopStatuses.Active);
        JourneyStopRow second = Batch7PlanRevisionTests.StopRow("s-2", 2, JourneyStopRoles.Pickup, JourneyStopStatuses.Pending);
        JourneyStopRow unload = Batch7PlanRevisionTests.StopRow("s-3", 3, JourneyStopRoles.Unload, JourneyStopStatuses.Pending);
        first.MovementLegId = "00000000-0000-4000-8000-000000074901";
        second.MovementLegId = "00000000-0000-4000-8000-000000074902";
        unload.MovementLegId = "00000000-0000-4000-8000-000000074903";
        JourneyStopRow[] stops = [first, second, unload];

        await StageAsync(context, "00000000-0000-4000-8000-000000074911",
            JourneyPlanBuilder.Plan(runtime, stops, first, arrivedAtCurrent: true, revision: 5));
        string before = await LastPayloadAsync(context);

        first.Status = JourneyStopStatuses.Completed;
        UpcomingStopPlanProjection advanced = JourneyPlanBuilder.Plan(runtime, stops, second, arrivedAtCurrent: false, revision: 6);
        Assert.False(JourneyRuntimeEngine.PlanOnTheWireMatches(before, advanced));

        await StageAsync(context, "00000000-0000-4000-8000-000000074912", advanced);
        string after = await LastPayloadAsync(context);
        Assert.True(JourneyRuntimeEngine.PlanOnTheWireMatches(after, advanced));
        using JsonDocument document = JsonDocument.Parse(after);
        Assert.Equal(
            [(second.MovementLegId, 1, "ACTIVE"), (unload.MovementLegId, 2, "PLANNED")],
            document.RootElement.GetProperty("payload").GetProperty("legs").EnumerateArray()
                .Select(leg => (leg.GetProperty("movementLegId").GetString(), leg.GetProperty("sequence").GetInt32(),
                    leg.GetProperty("state").GetString())));
    }

    /// <summary>
    /// 清桩途中那张计划不经 <see cref="JourneyPlanBuilder.Plan"/>，线上那一份照样带着已完成的桩腿：车载端清桩入口靠它认桩号
    /// （hmi <c>WireToGateStationClearance</c>，control-server#406）。
    /// </summary>
    [Fact]
    public void TheClearancePlanStillCarriesTheCompletedChargerLegOnTheWire()
    {
        JourneyRuntimeRow runtime = Batch7PlanRevisionTests.PlanRuntime();
        JourneyStopRow charger = Batch7PlanRevisionTests.StopRow("charger", 1, JourneyStopRoles.Pickup, JourneyStopStatuses.Completed);
        JourneyStopRow move = Batch7PlanRevisionTests.StopRow("move", 2, JourneyStopRoles.Pickup, JourneyStopStatuses.Active);

        IReadOnlyList<UpcomingMovementLeg> wire = OnboardJourneyPublisher.WireLegs(new UpcomingStopPlanProjection(
            3, [JourneyPlanBuilder.ChargerLeg(runtime, charger, "COMPLETED"), JourneyPlanBuilder.IdleReturnLeg(runtime, move, arrived: false)]));

        Assert.Equal(
            [(JourneyPlanBuilder.ChargerStopPurpose, "COMPLETED"), (JourneyPlanBuilder.WaitingPointStopPurpose, "ACTIVE")],
            wire.Select(leg => (leg.StopPurposeCategory, leg.State)));
    }

    // ---- 构造 ------------------------------------------------------------------------------------

    private static async Task StageAsync(ControlServerDbContext context, string messageId, UpcomingStopPlanProjection plan)
    {
        await OnboardJourneyPublisher.StageUpcomingStopPlanAsync(
            new WireToGateStore(context), messageId, "AGV-001", 1, plan, StagedAt, Token);
        await context.SaveChangesAsync(Token);
    }

    private static async Task<string> LastPayloadAsync(ControlServerDbContext context) =>
        (await context.ProtocolOutbox.AsNoTracking().ToArrayAsync(Token))
        .OrderBy(row => row.MessageId, StringComparer.Ordinal)
        .Last()
        .PayloadJson;

    private static async Task<ControlServerDbContext> ContextAsync(SqliteConnection connection)
    {
        ControlServerDbContext context = new(new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync(Token);
        return context;
    }

    /// <summary>站号 10, 12, 14 … 递增的一串取货停靠；前 <paramref name="currentNextStopIndex"/> 个是走过的。</summary>
    private static EnRouteVehiclePlan PlanFrom(int currentNextStopIndex, EnRouteStop[] stops) =>
        new(stops, 0, currentNextStopIndex, stops.ToDictionary(stop => stop.StopId, _ => 1, StringComparer.Ordinal));

    private static EnRouteStop[] PickupChain(int count) =>
        [.. Enumerable.Range(0, count).Select(index =>
            new EnRouteStop($"s-{index}", $"ST-{10 + (index * 2)}", 10 + (index * 2), ZoneA, JourneyStopRoles.Pickup))];

    /// <summary>新需求在链尾之后取货、再往后卸货：顺路，代价不会挡它。</summary>
    private static EnRouteAppendCandidate Candidate() => new(
        new EnRouteStop("new-p", "ST-90", 90, ZoneA, JourneyStopRoles.Pickup),
        new EnRouteStop("new-u", "ST-92", 92, ZoneA, JourneyStopRoles.Unload),
        ZoneA);

    private static DispatchZoneParameterTableVersion Zones() =>
        new(
            Version: 7,
            ContentSha256: new string('0', 64),
            SnapshotId: null,
            LoadedAt: DateTimeOffset.UnixEpoch,
            Source: "test",
            new Dictionary<string, DispatchZoneParameters>(StringComparer.Ordinal)
            {
                [ZoneA] = new DispatchZoneParameters(ZoneA, 10_000_000, null)
            });
}
