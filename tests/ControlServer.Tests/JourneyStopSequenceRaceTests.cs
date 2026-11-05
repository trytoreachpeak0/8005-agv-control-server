using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 两个上下文先后改同一趟旅程的停靠序位，结果必须仍是一组连续、不重号的数（control-server#879）。
/// </summary>
/// <remarks>
/// <para>
/// <b>现场（11-05 agv02，生产 <c>9c8c34a5</c>）。</b>20:32:35 UTC 那一轮里，操作员在车载屏上取消了一条还没装的需求（bb32），
/// 车载端连接上的计划修订删掉它的卸货停靠、把后面的停靠往前挪一位并提交；同一时刻派车轮在引擎的上下文里给这趟旅程追加一条需求（9974），
/// 新停靠插在前面、把后面的停靠往后挪一位。一删一插正好抵消，于是 N4-1、N2-2、N2-4 三个停靠「新的序位」恰好等于引擎上下文里
/// 本轮早先跟踪着的「旧的序位」。EF 按身份解析把跟踪着的旧实例交回来，赋值等于旧值就不发 UPDATE，库里留下的是修订写的值——
/// 追加写的其余停靠与它们撞号：两个 5、缺 8。之后推进每轮抛 <c>Sequence contains no elements</c>，车带着三处货停了近 4 小时。
/// 推算的每一个序位都与改库前的备份（<c>evidence/cs879/</c>）逐行对上。
/// </para>
/// <para>
/// 这里用最小的形状复现同一个机理：锚需求 A 取货 101（已完成）、卸货 202（当前下一站）；B、C 已装车，卸货 250、210；
/// D 还没装，取货 205 排在当前下一站之后，卸货与 B 共用 250——所以 D 离开只删一个停靠。追加的 E 取货 226 新开一个停靠、卸货并进 250，
/// 只插一个停靠。删一个、插一个，与现场一样互相抵消。
/// </para>
/// </remarks>
public sealed class JourneyStopSequenceRaceTests
{
    private const string Anchor = "D-8790";
    private const string LoadedFar = "D-8791";
    private const string LoadedNear = "D-8792";
    private const string Leaving = "D-8793";
    private const string Appended = "D-8794";
    private const string Agv = "agv-01";
    private const string VehicleKey = "VK-01";
    private const string Zone = "MAP-25-WIRE_TO_GATE";
    private const int AppendedPickupStation = 226;

    private static readonly Func<int, int, long?> Distance = (from, to) => Math.Abs(from - to) * 1000L;

    /// <summary>
    /// 现场那一轮：修订在另一个连接上先提交，追加在早先已经跟踪过这些停靠的上下文里后写。
    /// </summary>
    [Fact]
    public async Task AnAppendWrittenAfterARevisionOnAnotherConnectionLeavesContiguousSequences()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        JourneyRuntimeRow runtime = await SeedAsync(fixture);

        // 派车轮与引擎共用的上下文：本轮早先读过这趟旅程的停靠（让站、到站判定都用这个带跟踪的读法）。
        await using ControlServerDbContext round = fixture.NewContext();
        await StationYield.StopsOfAsync(round, runtime.JourneyId, token);

        // 车载端连接：操作员取消还没装的 D，删掉它的取货停靠并提交。
        await using (ControlServerDbContext onboard = fixture.NewContext())
        {
            await new PickupStopTermination(onboard)
                .StageAsync(runtime, Leaving, "CANCELLED_BY_OPERATOR", Batch7JourneyFixture.Now, token);
            await onboard.SaveChangesAsync(token);
        }

        // 追加按库里此刻的计划算插位（游标不带跟踪），再用本轮的上下文写。
        JourneyAppendPlan append = await PlanAppendAsync(fixture, runtime);
        await new WireToGateStore(round).AppendToJourneyAsync(
            Batch7JourneyFixture.Snapshot(Appended, Batch7JourneyFixture.Now.AddMinutes(5)), append, token);

        AssertContiguous(await SequencesAsync(fixture, runtime));
    }

    /// <summary>
    /// 反过来：追加在另一个上下文先提交，计划修订在早先已经跟踪过这些停靠的上下文里后写。
    /// </summary>
    [Fact]
    public async Task ARevisionWrittenAfterAnAppendOnAnotherConnectionLeavesContiguousSequences()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        JourneyRuntimeRow runtime = await SeedAsync(fixture);

        await using ControlServerDbContext onboard = fixture.NewContext();
        await StationYield.StopsOfAsync(onboard, runtime.JourneyId, token);

        JourneyAppendPlan append = await PlanAppendAsync(fixture, runtime);
        await using (ControlServerDbContext round = fixture.NewContext())
        {
            await new WireToGateStore(round).AppendToJourneyAsync(
                Batch7JourneyFixture.Snapshot(Appended, Batch7JourneyFixture.Now.AddMinutes(5)), append, token);
        }

        await new PickupStopTermination(onboard)
            .StageAsync(runtime, Leaving, "CANCELLED_BY_OPERATOR", Batch7JourneyFixture.Now, token);
        await onboard.SaveChangesAsync(token);

        AssertContiguous(await SequencesAsync(fixture, runtime));
    }

    private static void AssertContiguous((string StopId, int Sequence)[] stops) =>
        Assert.True(
            stops.Select(stop => stop.Sequence).Order().SequenceEqual(Enumerable.Range(1, stops.Length)),
            "sequences: " + string.Join(", ", stops.Select(stop => $"{stop.StopId}={stop.Sequence}")));

    private static async Task<(string StopId, int Sequence)[]> SequencesAsync(Batch7JourneyFixture fixture, JourneyRuntimeRow runtime)
    {
        await using ControlServerDbContext context = fixture.NewContext();
        return [.. (await context.Set<JourneyStopRow>().AsNoTracking()
                .Where(row => row.JourneyId == runtime.JourneyId)
                .ToArrayAsync(TestContext.Current.CancellationToken))
            .OrderBy(row => row.Sequence).ThenBy(row => row.StopId, StringComparer.Ordinal)
            .Select(row => (row.StopId, row.Sequence))];
    }

    /// <summary>
    /// 与 <c>EnRouteAppendCriterion</c> 同一个算法、同一份输入：库里此刻的游标（不带跟踪）交给 <see cref="DispatchRoundRunner.EnRoutePlanOf"/>，
    /// 再交给 <see cref="EnRouteAppendPlanner"/>；落库那一份照 <c>DispatchRoundRunner.AppendToJourneyAsync</c> 转换。
    /// </summary>
    private static async Task<JourneyAppendPlan> PlanAppendAsync(Batch7JourneyFixture fixture, JourneyRuntimeRow runtime)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        DateTimeOffset at = Batch7JourneyFixture.Now.AddMinutes(5);
        JourneyExecutionPlan demand = Batch7JourneyFixture.Plan(Appended, Agv, VehicleKey, at) with
        {
            PickupStationId = $"ST-{AppendedPickupStation}",
            PickupStationRiotId = AppendedPickupStation,
            GateStationId = "ST-250",
            GateStationRiotId = 250,
        };
        string key = demand.DerivationKeyFor(Appended);
        JourneyStopCursor cursor;
        await using (ControlServerDbContext context = fixture.NewContext())
        {
            cursor = await JourneyStopCursor.LoadAsync(context, runtime, token);
        }

        EnRouteAppendDecision decision = EnRouteAppendPlanner.Plan(
            DispatchRoundRunner.EnRoutePlanOf(cursor, vehicleStation: 202, loadingPhaseClosed: false),
            new EnRouteAppendCandidate(
                new EnRouteStop(
                    JourneyIdentity.AppendedPickupStopId(key), $"ST-{AppendedPickupStation}", AppendedPickupStation, Zone,
                    JourneyStopRoles.Pickup),
                new EnRouteStop(JourneyIdentity.AppendedUnloadStopId(key), "ST-250", 250, Zone, JourneyStopRoles.Unload),
                Zone),
            new DispatchZoneParameterTableVersion(
                7, "sha-879", null, Batch7JourneyFixture.Now, "test",
                new Dictionary<string, DispatchZoneParameters>(StringComparer.Ordinal)
                {
                    [Zone] = new(Zone, EnRouteAdditionMaxPathCostIncrease: 1_000_000, StarvationThresholdSeconds: null)
                }),
            Distance);
        EnRouteAppendPlacement placement = decision.Placement
            ?? throw new InvalidOperationException($"the append was refused: {decision.RefusalReasonCode}");
        // 前提：取货新开一个停靠、卸货并进 B 的 250——只插一个停靠，与删掉的一个抵消。
        Assert.Null(placement.MergeIntoPickupStopId);
        Assert.Equal("B-UNLOAD", placement.MergeIntoUnloadStopId);

        return new JourneyAppendPlan(
            runtime.JourneyId,
            Appended,
            demand,
            JourneyIdentity.AppendedPickupStopId(key),
            placement.MergeIntoUnloadStopId!,
            Zone,
            7,
            [.. placement.Resequenced.Select(stop => new JourneyStopSequenceChange(stop.StopId, stop.Sequence))],
            at);
    }

    /// <summary>
    /// 锚需求经真实受理路径建出旅程（取货 101、卸货 202），再挂三条需求：B、C 已装车（卸货 250、210），D 没装（取货 205，卸货并在 250）。
    /// 锚的取货停靠已完成，当前下一站是 202；序位 101=1、202=2、205=3、250=4、210=5。
    /// </summary>
    private static async Task<JourneyRuntimeRow> SeedAsync(Batch7JourneyFixture fixture)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, Anchor, Agv, VehicleKey, Batch7JourneyFixture.Now);
        JourneyRuntimeRow runtime = await fixture.Context.JourneyRuntimes.AsNoTracking().SingleAsync(token);
        foreach (string demandId in new[] { LoadedFar, LoadedNear, Leaving })
        {
            await JourneyMembershipSeed.AddFurtherDemandAsync(fixture.Context, runtime, demandId);
        }

        JourneyStopRow[] seeded = await fixture.Context.Set<JourneyStopRow>().Where(row => row.JourneyId == runtime.JourneyId)
            .ToArrayAsync(token);
        seeded.Single(row => row.StopRole == JourneyStopRoles.Pickup).Status = JourneyStopStatuses.Completed;
        string anchorUnload = seeded.Single(row => row.StopRole == JourneyStopRoles.Unload).StopId;
        fixture.Context.Set<JourneyStopRow>().AddRange(
            Stop(runtime, "D-PICKUP", JourneyStopRoles.Pickup, 205, 3),
            Stop(runtime, "B-UNLOAD", JourneyStopRoles.Unload, 250, 4),
            Stop(runtime, "C-UNLOAD", JourneyStopRoles.Unload, 210, 5));

        JourneyDemandRow[] memberships = await fixture.Context.Set<JourneyDemandRow>()
            .Where(row => row.JourneyId == runtime.JourneyId).ToArrayAsync(token);
        memberships.Single(row => row.DemandId == Anchor).Status = JourneyDemandStatuses.Loaded;
        Point(memberships.Single(row => row.DemandId == LoadedFar), anchorUnload, "B-UNLOAD", JourneyDemandStatuses.Loaded);
        Point(memberships.Single(row => row.DemandId == LoadedNear), anchorUnload, "C-UNLOAD", JourneyDemandStatuses.Loaded);
        Point(memberships.Single(row => row.DemandId == Leaving), "D-PICKUP", "B-UNLOAD", JourneyDemandStatuses.PendingLoad);
        await fixture.Context.SaveChangesAsync(token);
        fixture.Context.ChangeTracker.Clear();
        return runtime;
    }

    private static void Point(JourneyDemandRow membership, string pickupStopId, string unloadStopId, string status)
    {
        membership.PickupStopId = pickupStopId;
        membership.UnloadStopId = unloadStopId;
        membership.Status = status;
    }

    private static JourneyStopRow Stop(JourneyRuntimeRow runtime, string stopId, string role, int station, int sequence) => new()
    {
        StopId = stopId,
        JourneyId = runtime.JourneyId,
        Sequence = sequence,
        StopRole = role,
        StationId = $"ST-{station}",
        StationRiotId = station,
        DispatchZone = Zone,
        OperationSessionId = JourneyPlanBuilder.StableGuid(stopId, "session"),
        MovementLegId = JourneyPlanBuilder.StableGuid(stopId, "leg"),
        UpperId = $"W2G-{stopId}",
        VehicleBusinessMessageId = JourneyPlanBuilder.StableGuid(stopId, "vehicle-state"),
        WorklistMessageId = JourneyPlanBuilder.StableGuid(stopId, "worklist"),
        PlanMessageId = JourneyPlanBuilder.StableGuid(stopId, "plan"),
        Status = JourneyStopStatuses.Pending,
        CreatedAt = runtime.CreatedAt
    };
}
