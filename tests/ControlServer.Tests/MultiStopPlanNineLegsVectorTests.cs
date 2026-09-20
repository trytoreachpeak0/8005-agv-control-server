using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// <c>CV-MULTI-STOP-PLAN-NINE-LEGS</c> 的服务端半边（<c>FP-IS-08</c>，批次7-06，control-server#211）。
/// </summary>
/// <remarks>
/// <para>
/// 向量对服务端的三条断言各有一条测试：<c>PLAN_UP_TO_NINE_LEGS</c>（一张计划最多九条腿）、
/// <c>ORDER_LEGS_BY_SEQUENCE</c>（腿按序位连续排）、<c>CATEGORISE_EVERY_STOP_PURPOSE</c>（每条腿都有用途分类，
/// 本批的业务停靠一律 <c>BUSINESS</c>）。
/// </para>
/// <para>
/// <b>三条都驱动真实的计划生成与发布，不验 payload 形状。</b>断言读的是发件箱里那条 <c>UpcomingStopPlanSnapshot</c>
/// 的载荷——也就是车上真会收到的那一份——而计划是推进段按库里的停靠行算出来的。造一个投影对象直接断言它的字段，
/// 证的只是那个对象的构造函数。
/// </para>
/// </remarks>
public sealed class MultiStopPlanNineLegsVectorTests
{
    /// <summary>一张计划最多九条腿：协议 <c>UpcomingStopPlanSnapshot.legs</c> 的上限，也是追加门禁守的那一条。</summary>
    [Fact]
    [Trait("ProtocolVector", "CV-MULTI-STOP-PLAN-NINE-LEGS")]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ThePlanCarriesUpToNineLegs()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JsonElement plan = await PlanWithStopsAsync(fixture, stopCount: 9);

        Assert.Equal(9, plan.GetProperty("legs").GetArrayLength());
    }

    /// <summary>腿按序位排，而且序位从 1 起连续——换序之后车上看到的次序就是计划的次序。</summary>
    [Fact]
    [Trait("ProtocolVector", "CV-MULTI-STOP-PLAN-NINE-LEGS")]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task TheLegsAreOrderedBySequence()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JsonElement plan = await PlanWithStopsAsync(fixture, stopCount: 5);

        int[] sequences = [.. plan.GetProperty("legs").EnumerateArray()
            .Select(leg => leg.GetProperty("sequence").GetInt32())];
        Assert.Equal([1, 2, 3, 4, 5], sequences);
    }

    /// <summary>
    /// 每条腿都带用途分类，本批的业务停靠一律 <c>BUSINESS</c>：等待点是 FP-C4、充电桩是 FP-C1，这个运行时都还不产生，
    /// 所以这个常量是关于本剖面的一个事实，不是占位符。
    /// </summary>
    [Fact]
    [Trait("ProtocolVector", "CV-MULTI-STOP-PLAN-NINE-LEGS")]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task EveryLegCarriesItsStopPurposeCategory()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JsonElement plan = await PlanWithStopsAsync(fixture, stopCount: 4);

        Assert.All(
            plan.GetProperty("legs").EnumerateArray(),
            leg => Assert.Equal("BUSINESS", leg.GetProperty("stopPurposeCategory").GetString()));
    }

    /// <summary>
    /// 受理一趟旅程，往它的停靠序列里补到 <paramref name="stopCount"/> 个停靠，跑到取货到站，返回那一版计划的载荷。
    /// </summary>
    /// <remarks>
    /// 停靠直接写库：途中追加是本票的另一半，而这三条要问的是「计划投影拿到 N 个停靠时发出来什么」，
    /// 不是「追加怎么把停靠加进去」。两者分开，这三条在追加那一半还没接上时也立得住。
    /// </remarks>
    private static async Task<JsonElement> PlanWithStopsAsync(RuntimeFixture fixture, int stopCount)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(FirstDemandId);

        JourneyStopRow unload = await fixture.Context.Set<JourneyStopRow>()
            .SingleAsync(row => row.StopId == JourneyIdentity.UnloadStopId(runtime.JourneyId), token);
        // 受理写下两个停靠，其余的补在卸货停靠之前——插在当前下一站之后，与追加落下的形状一致。
        for (int index = 0; index < stopCount - 2; index++)
        {
            string stopId = $"{runtime.JourneyId}|EXTRA-{index}";
            fixture.Context.Set<JourneyStopRow>().Add(new JourneyStopRow
            {
                StopId = stopId,
                JourneyId = runtime.JourneyId,
                Sequence = 2 + index,
                StopRole = JourneyStopRoles.Pickup,
                StationId = runtime.PickupStationId,
                StationRiotId = runtime.PickupStationRiotId,
                DispatchZone = runtime.DispatchZone,
                OperationSessionId = JourneyPlanBuilder.StableGuid(stopId, "session"),
                MovementLegId = JourneyPlanBuilder.StableGuid(stopId, "leg"),
                UpperId = $"W2G-{stopId}",
                VehicleBusinessMessageId = JourneyPlanBuilder.StableGuid(stopId, "vehicle-state"),
                WorklistMessageId = JourneyPlanBuilder.StableGuid(stopId, "worklist"),
                PlanMessageId = JourneyPlanBuilder.StableGuid(stopId, "plan"),
                SublotRequestMessageId = JourneyPlanBuilder.StableGuid(stopId, "sublot-request"),
                DepartureSafetyCheckMessageId = JourneyPlanBuilder.StableGuid(stopId, "safety-request"),
                DepartureSafetyCheckId = JourneyPlanBuilder.StableGuid(stopId, "safety-check"),
                Status = JourneyStopStatuses.Pending,
                CreatedAt = runtime.CreatedAt
            });
        }

        unload = await fixture.Context.Set<JourneyStopRow>()
            .SingleAsync(row => row.StopId == JourneyIdentity.UnloadStopId(runtime.JourneyId), token);
        unload.Sequence = stopCount;
        await fixture.Context.SaveChangesAsync(token);

        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupUpperId, runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        await TickAndRunAsync(fixture);

        // 取货到站那一版计划：停靠行上的 PlanMessageId 就是它的第一版。
        JourneyStopRow pickup = await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(row => row.StopId == JourneyIdentity.PickupStopId(runtime.JourneyId), token);
        ProtocolOutboxRow row = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(item => item.MessageId == pickup.PlanMessageId, token);
        using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
        return document.RootElement.GetProperty("payload").Clone();
    }
}
