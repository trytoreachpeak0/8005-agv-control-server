using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Infrastructure.Persistence;
using static ControlServer.Tests.ChargingAllocationTests;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

/// <summary>
/// 清桩计划在线上的腿序号从 1 连续（control-server#664）。清桩移动停靠的序号是 <c>charger.Sequence + attempt</c>，到点收尾那张计划只有这一条腿，
/// 修之前发出去是 <c>[2]</c>；第二次清桩途中是 <c>[1, 3]</c>。hmi#327 之前的车载端要求排序后等于 1..n，拒收、断会话、服务端每个新会话重放，
/// 现场约每 4 秒重连一次。库里停靠的序号不动。
/// </summary>
public sealed partial class ChargingClearanceToWaitingPointTests
{
    /// <summary>第一次清桩：途中计划 <c>[1, 2]</c>，到点收尾的单腿计划 <c>[1]</c>；停靠行上的序号仍是 2，腿 id 与停靠一致。</summary>
    [Fact]
    public async Task TheFirstClearanceSendsItsPlansNumberedFromOne()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync();
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        JourneyStopRow move = Assert.Single(await ClearanceStopsAsync(fleet, journey));

        Assert.Equal(OneTwo, Sequences((await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot"))[^1]));

        fleet.Riot.CompleteOrder(move.UpperId);
        StandOn(fleet, 214);
        await RoundAsync(fleet);

        Assert.NotNull((await ClearanceAsync(fleet)).CompletedAt);
        JsonElement closingPlan = (await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot"))[^1];
        JsonElement leg = Assert.Single(closingPlan.GetProperty("legs").EnumerateArray());
        Assert.Equal((1, move.MovementLegId, "ARRIVED"),
            (leg.GetProperty("sequence").GetInt32(), leg.GetProperty("movementLegId").GetString(), leg.GetProperty("state").GetString()));
        Assert.Equal(2, Assert.Single(await ClearanceStopsAsync(fleet, journey)).Sequence);
    }

    /// <summary>
    /// 第二次清桩（第一次的点证明没单没动而放掉）：途中计划仍是 <c>[1, 2]</c>，第二条腿是第二次移动的停靠（库里序号 3）。
    /// </summary>
    [Fact]
    public async Task ASecondClearanceAttemptSendsItsPlanNumberedFromOne()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync(points: [Point214, Point300]);
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        await RoundAsync(fleet);
        fleet.Riot.AnswersAbsentAsRealRiot = true;
        fleet.Riot.CreateAnswer = intent => fleet.Riot.RealRiotAbsent(intent.UpperId);
        await RoundAsync(fleet);
        fleet.Riot.CreateAnswer = null;
        for (int round = 0; round < 7; round++)
        {
            await fleet.HearFromEveryVehicleAsync();
            await fleet.RunRoundAsync(TimeSpan.FromSeconds(30));
        }
        JourneyStopRow second = Assert.Single(await ClearanceStopsAsync(fleet, journey),
            stop => stop.UpperId == ClearanceMoveShape.UpperIdFor(journey.JourneyId, 2));
        Assert.Equal(3, second.Sequence);

        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(TimeSpan.FromSeconds(30));

        JsonElement plan = (await PayloadsAsync(fleet, AgvA, "UpcomingStopPlanSnapshot"))[^1];
        Assert.Equal(OneTwo, Sequences(plan));
        Assert.Equal(second.MovementLegId, plan.GetProperty("legs")[1].GetProperty("movementLegId").GetString());
    }

    private static readonly int[] OneTwo = [1, 2];

    private static int[] Sequences(JsonElement plan) =>
        [.. plan.GetProperty("legs").EnumerateArray().Select(leg => leg.GetProperty("sequence").GetInt32())];
}
