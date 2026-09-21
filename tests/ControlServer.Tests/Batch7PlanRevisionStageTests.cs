using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 终结一条需求之后的计划修订落到库里：删掉它留下的空停靠，给了路网时再把后面的停靠换成更省的顺序
/// （批次7-10，control-server#215，REQ-0197，调度决策 1、6）。
/// </summary>
/// <remarks>
/// <para>
/// 站点是一维的：站号就是坐标，两站代价是站号之差乘一千毫米，与 <see cref="Batch7PlanRevisionTests"/> 同一把尺。
/// 旅程此刻在锚需求的卸货停靠（202）；后面依次是 B 的卸货（250）、C 的卸货（210）、D 的取货（205）。B、C 已装车。
/// </para>
/// <para>
/// 终结 D：它的取货停靠没有别的作业，删。剩下 202 → 250 → 210 全程 48+40=88；换成 202 → 210 → 250 是 8+40=48，
/// B 的前缀由 48 变 48、C 的由 88 变 8，没有任何增量——所以分区参数不配置也照换。不给路网时只删不换，顺序原样。
/// 两条用例只差路网给没给，所以「换了」只能是路网带来的。
/// </para>
/// </remarks>
public sealed class Batch7PlanRevisionStageTests
{
    private const string Anchor = "D-7401";
    private const string LoadedFar = "D-7402";
    private const string LoadedNear = "D-7403";
    private const string Leaving = "D-7404";
    private const string Zone = "MAP-25-WIRE_TO_GATE";

    private static readonly Func<int, int, long?> Distance = (from, to) => Math.Abs(from - to) * 1000L;

    [Fact]
    public async Task WithARouteTheStopsAfterTheCurrentOneAreReorderedOnceTheEmptyStopIsGone()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        JourneyRuntimeRow runtime = await SeedAsync(fixture);

        await new PickupStopTermination(fixture.Context, new PlanRevisionRouting(null, Distance))
            .StageAsync(runtime, Leaving, "CANCELLED_BY_OPERATOR", Batch7JourneyFixture.Now, TestContext.Current.CancellationToken);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [(101, JourneyStopStatuses.Completed, 1), (202, JourneyStopStatuses.Pending, 2), (210, JourneyStopStatuses.Pending, 3),
             (250, JourneyStopStatuses.Pending, 4), (205, JourneyStopStatuses.Removed, 5)],
            await StopsAsync(fixture));
    }

    [Fact]
    public async Task WithoutARouteTheEmptyStopIsStillRemovedButNothingIsReordered()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        JourneyRuntimeRow runtime = await SeedAsync(fixture);

        await new PickupStopTermination(fixture.Context)
            .StageAsync(runtime, Leaving, "CANCELLED_BY_OPERATOR", Batch7JourneyFixture.Now, TestContext.Current.CancellationToken);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [(101, JourneyStopStatuses.Completed, 1), (202, JourneyStopStatuses.Pending, 2), (250, JourneyStopStatuses.Pending, 3),
             (210, JourneyStopStatuses.Pending, 4), (205, JourneyStopStatuses.Removed, 5)],
            await StopsAsync(fixture));
    }

    /// <summary>
    /// 删过一次停靠之后再修订：当前下一站的序位不动，车在站上时这一站的清单号也不跳（审查 M1）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 旧的编号从「已完成或已删的最大序位 + 1」往后数。第一次修订删掉 205 之后它留着序位 5；第二次修订时它算进那个
    /// 最大值，当前下一站 202 就从 2 变成 6。清单号是「序位比它小的停靠各发几版之和」
    /// （<see cref="JourneyStopCursor.FirstWorklistRevisionAt"/>），于是车停在 202 时这一站的号、录入地址的区间、
    /// 第一版的消息 id 全都跳了——升版前已经发出的录入不再被认作这一站的。
    /// </para>
    /// <para>
    /// 第二次修订不删任何东西（没有需求离开），只让它按新的编号规则把序位写一遍，所以两次读到的差别只能来自编号。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASecondRevisionKeepsTheCurrentStopAndItsWorklistRevisionWhereTheyWere()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        JourneyRuntimeRow runtime = await SeedAsync(fixture);
        await new PickupStopTermination(fixture.Context, new PlanRevisionRouting(null, Distance))
            .StageAsync(runtime, Leaving, "CANCELLED_BY_OPERATOR", Batch7JourneyFixture.Now, token);
        await fixture.Context.SaveChangesAsync(token);
        (int Sequence, long FirstRevision) before = await CurrentStopAsync(fixture, runtime);
        Assert.Equal(2, before.Sequence);

        await JourneyPlanRevisionStage.StageAsync(
            fixture.Context, runtime.JourneyId, [], currentStopMayGo: false, new PlanRevisionRouting(null, Distance), token);
        await fixture.Context.SaveChangesAsync(token);

        Assert.Equal(before, await CurrentStopAsync(fixture, runtime));
        // 序位仍是一组不重号的连续数，删掉的排在所有开放的之后。
        (int Station, string Status, int Sequence)[] stops = await StopsAsync(fixture);
        Assert.Equal([1, 2, 3, 4, 5], stops.Select(stop => stop.Sequence).Order());
        Assert.True(stops.Where(stop => stop.Status == JourneyStopStatuses.Removed).Min(stop => stop.Sequence)
            > stops.Where(stop => stop.Status == JourneyStopStatuses.Pending).Max(stop => stop.Sequence));
    }

    /// <summary>
    /// 派车轮次交给途中追加规划器的计划：当前下一站之后已删的停靠不在停靠表里，单独交（审查 M3）。
    /// </summary>
    /// <remarks>
    /// 修前 <c>DispatchRoundRunner</c> 把整条旅程原样交出去，已删的 205 就成了规划器眼里一个还要去的站——进路径代价、
    /// 分区连续与腿数上限。规划器怎么对待单独交来的已删停靠由 <c>Batch7EnRouteAppendPlannerTests.RemovedStopsAfterTheCurrentOneAreNotLegsAndAreNumberedLast</c> 守。
    /// </remarks>
    [Fact]
    public async Task TheAppendPlannerIsNotHandedARemovedStopAsAStopToVisit()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        JourneyRuntimeRow runtime = await SeedAsync(fixture);
        await new PickupStopTermination(fixture.Context, new PlanRevisionRouting(null, Distance))
            .StageAsync(runtime, Leaving, "CANCELLED_BY_OPERATOR", Batch7JourneyFixture.Now, token);
        await fixture.Context.SaveChangesAsync(token);
        JourneyStopCursor cursor = await JourneyStopCursor.LoadAsync(fixture.NewContext(), runtime, token);
        string removed = cursor.Stops.Single(stop => stop.Status == JourneyStopStatuses.Removed).StopId;

        EnRouteVehiclePlan plan = DispatchRoundRunner.EnRoutePlanOf(cursor, vehicleStation: 202, loadingPhaseClosed: false);

        Assert.DoesNotContain(removed, plan.Stops.Select(stop => stop.StopId));
        Assert.Equal([removed], plan.TrailingRemovedStopIds!);
        Assert.Equal(cursor.Current.StopId, plan.Stops[plan.CurrentNextStopIndex].StopId);
    }

    /// <summary>当前下一站的序位，与车停在它上面时它第一版清单的号数（旅程基准取 100）。</summary>
    private static async Task<(int Sequence, long FirstRevision)> CurrentStopAsync(
        Batch7JourneyFixture fixture, JourneyRuntimeRow runtime)
    {
        JourneyStopCursor cursor = await JourneyStopCursor.LoadAsync(
            fixture.NewContext(), runtime, TestContext.Current.CancellationToken);
        return (cursor.Current.Sequence, cursor.FirstWorklistRevisionAt(100, cursor.Current));
    }

    /// <summary>每个停靠的站号、状态、序位，按站号在库里的顺序排：先排开放的（按序位），删掉的放在最后。</summary>
    private static async Task<(int Station, string Status, int Sequence)[]> StopsAsync(Batch7JourneyFixture fixture) =>
        [.. (await fixture.NewContext().Set<JourneyStopRow>().AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken))
            .OrderBy(row => row.Status == JourneyStopStatuses.Removed)
            .ThenBy(row => row.Sequence)
            .Select(row => (row.StationRiotId, row.Status, row.Sequence))];

    /// <summary>
    /// 锚需求经真实受理路径建出旅程（取货 101、卸货 202），再挂三条需求与它们的停靠。锚的取货停靠已完成、锚已装车，
    /// 所以当前下一站是 202。
    /// </summary>
    private static async Task<JourneyRuntimeRow> SeedAsync(Batch7JourneyFixture fixture)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, Anchor, "agv-01", "VK-01", Batch7JourneyFixture.Now);
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
            Stop(runtime, "B-UNLOAD", JourneyStopRoles.Unload, 250, 3),
            Stop(runtime, "C-UNLOAD", JourneyStopRoles.Unload, 210, 4),
            Stop(runtime, "D-PICKUP", JourneyStopRoles.Pickup, 205, 5));

        JourneyDemandRow[] memberships = await fixture.Context.Set<JourneyDemandRow>()
            .Where(row => row.JourneyId == runtime.JourneyId).ToArrayAsync(token);
        memberships.Single(row => row.DemandId == Anchor).Status = JourneyDemandStatuses.Loaded;
        Point(memberships.Single(row => row.DemandId == LoadedFar), anchorUnload, "B-UNLOAD", JourneyDemandStatuses.Loaded);
        Point(memberships.Single(row => row.DemandId == LoadedNear), anchorUnload, "C-UNLOAD", JourneyDemandStatuses.Loaded);
        // D 还没取货，卸货与锚共用 202——所以 202 在 D 离开之后仍有剩余作业（锚还没卸），不会被删。
        Point(memberships.Single(row => row.DemandId == Leaving), "D-PICKUP", anchorUnload, JourneyDemandStatuses.PendingLoad);
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
