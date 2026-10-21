using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.ChargingAllocationTests;
using static ControlServer.Tests.ChargingExecutionTests;
using static ControlServer.Tests.ChargingUnableToChargeTests;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

/// <summary>
/// 清桩选点看点上有没有车（control-server#798）：选点之前只看占用记录，另一辆车停在没有占用行的等待点上，清桩车照样被派过去，
/// 开到点前停下等放行、一直堵在路上。现在与空闲返回同一个判法（<see cref="IdleReturnEvaluator.ExcludePointAsync"/>），车队里每辆车都算。
/// </summary>
public sealed partial class ChargingClearanceToWaitingPointTests
{
    private static string AgvB => FleetFixture.AgvIds[1];

    /// <summary>
    /// B 是一辆普通的空闲车，停在唯一登记的 214 上，点上没有占用行（人手开过去、换库之后）：清桩的 A 不去 214，原地排队并告警，告警里写着
    /// 214 被车站着（<see cref="IdleReturnReasons.PointStoodOn"/>）。B 开走之后照常出发去 214——点空着、没有占用行时不误伤。
    /// </summary>
    [Fact]
    public async Task AnIdleVehicleStandingOnTheOnlyPointWithoutARowKeepsTheClearingVehicleOffItUntilItLeaves()
    {
        await using FleetFixture fleet = await ClearanceFleetAsync(vehicles: 2);
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        fleet.Riot.VehicleOverrides[KeyB] = seen => seen with { CurrentStationId = 214 };

        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Empty(await ClearanceStopsAsync(fleet, journey));
        Assert.Null(await StationAsync(fleet, 214));
        Assert.Equal(ChargingExecutionReasons.ClearanceNoWaitingPoint, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Contains(
            $"214={IdleReturnReasons.PointStoodOn}({AgvB},{StandingSources.Read})",
            Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2301).Message,
            StringComparison.Ordinal);

        fleet.Riot.VehicleOverrides[KeyB] = seen => seen with { CurrentStationId = 300 };
        await RoundAsync(fleet);

        Assert.Equal(214, Assert.Single(await ClearanceStopsAsync(fleet, journey)).StationRiotId);
        Assert.Equal((KeyA, journey.JourneyId), ((await StationAsync(fleet, 214))!.VehicleKey, (await StationAsync(fleet, 214))!.JourneyId));
    }

    /// <summary>
    /// 停在 300 上的 B 不是空闲车：一张外来订单占着它，或它离线（RIoT 仍报它最后停的 300）。两种都不在派车轮的空闲车里，清桩照样不派 A 去
    /// 离桩最近的 300，而是去剩下的 214（从 211 到 300 是 5 米，到 214 是 14 米）。
    /// </summary>
    [Theory]
    [InlineData("foreign-order")]
    [InlineData("offline")]
    public async Task AVehicleOutsideTheIdleFleetStandingOnAPointKeepsTheClearingVehicleOffIt(string why)
    {
        await using FleetFixture fleet = await ClearanceFleetAsync(vehicles: 2, points: [Point214, Point300]);
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);
        bool connected = why != "offline";
        fleet.Riot.VehicleOverrides[KeyB] = seen => seen with { CurrentStationId = 300, Connected = connected };
        if (why == "foreign-order")
        {
            await HoldByForeignOrderAsync(fleet, AgvB, KeyB);
        }

        await RoundAsync(fleet);

        Assert.Equal(214, Assert.Single(await ClearanceStopsAsync(fleet, journey)).StationRiotId);
        Assert.Null(await StationAsync(fleet, 300));
    }

    /// <summary>
    /// B 先被读到停在 214，此后 RIoT 读不到它：选点按它上一次读到的站算，清桩不派 A 去 214（偏保守，control-server#798 第 2 条），告警里 214 的
    /// 来源是 <c>last-known</c>。两种读不到（审查 M1）：读取抛出；生产网关的形状——对 SDK 失败不抛，答一辆离线、地图为空、没有当前站的车。
    /// </summary>
    [Theory]
    [InlineData("throws")]
    [InlineData("gateway-unknown-vehicle")]
    public async Task AVehicleThatCannotBeReadCountsWhereItWasLastSeen(string unreadable)
    {
        await using FleetFixture fleet = await ClearanceFleetAsync(vehicles: 2);
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        fleet.Riot.VehicleOverrides[KeyB] = seen => seen with { CurrentStationId = 214 };
        await RoundAsync(fleet);

        fleet.Riot.CancelOrder(journey.PickupUpperId);
        fleet.Riot.VehicleOverrides[KeyB] = unreadable == "throws"
            ? _ => throw new HttpRequestException("RIoT did not answer for B.")
            : seen => seen with { CurrentStationId = null, Connected = false, CurrentMap = "" };
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Empty(await ClearanceStopsAsync(fleet, journey));
        Assert.Null(await StationAsync(fleet, 214));
        Assert.Equal(ChargingExecutionReasons.ClearanceNoWaitingPoint, (await ChargingJourneyAsync(fleet, AgvA))!.BlockReasonCode);
        Assert.Contains(
            $"214={IdleReturnReasons.PointStoodOn}({AgvB},{StandingSources.LastKnown})",
            Assert.Single(fleet.EngineLog.Entries, entry => entry.EventId.Id == 2301).Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 清桩车自己不挡点（审查 L1）：等待点 216 与 A 停着的桩 211 在路网同一个节点 6、代价为 0。A 站在那个位置上，但「点上有车」只看别的车，
    /// A 照常预占 216。
    /// </summary>
    [Fact]
    public async Task TheClearingVehicleDoesNotKeepItselfOffAPointAtItsChargersNode()
    {
        WaitingPointEntry point216 = new(Map, 216, "等待点216", true, []);
        await using FleetFixture fleet = await ClearanceFleetAsync(points: [point216]);
        await fleet.ReplaceRouteGraphAsync(Edges(), [.. Stations(), new(216, point216.StationName, 5, 0, 5000, 6, 0)]);
        JourneyRuntimeRow journey = await OldOrderEndedAsync(fleet);

        await RoundAsync(fleet);

        Assert.Equal(216, Assert.Single(await ClearanceStopsAsync(fleet, journey)).StationRiotId);
        Assert.Equal((KeyA, journey.JourneyId), ((await StationAsync(fleet, 216))!.VehicleKey, (await StationAsync(fleet, 216))!.JourneyId));
    }

    /// <summary>挡着车的外来订单行直接写进库，与派车轮测它的写法相同（<c>AVehicleAForeignOrderHoldsTakesNoAppendedDemandUntilTheOrderHasEnded</c>）。</summary>
    private static async Task HoldByForeignOrderAsync(FleetFixture fleet, string agvId, string vehicleKey)
    {
        fleet.Context.ForeignRiotOrders.Add(new ForeignRiotOrderRow
        {
            RiotOrderId = "order-foreign-0798",
            UpperId = "MES-FIELD-0798",
            AgvId = agvId,
            DeviceKey = vehicleKey,
            Ownership = ForeignRiotOrderOwnership.Foreign,
            OwnershipBasis = "TEST",
            State = ForeignRiotOrderStates.StillRunningAfterCancel,
            DetectedAt = DateTimeOffset.UnixEpoch,
            LastSeenRunningAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });
        await fleet.Context.SaveChangesAsync(Token);
        fleet.Context.ChangeTracker.Clear();
    }
}
