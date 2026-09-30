using ControlServer.Application;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Charging;

/// <summary>这一轮读到的占用事实；桩的结论由 <see cref="Judge"/> 给。</summary>
/// <remarks>
/// 三样事实合起来才答得出「这个桩此刻可确认空闲」：本项目每辆车的位置、RIoT 上还没结束的订单清单、那些订单的目的站。
/// 任何一样读不全（<see cref="Unknown"/> 非空），每一个桩都答「未知」——未知不分桩：一辆不知道在哪的车可能正停在任何一个桩上。
/// </remarks>
public sealed class ChargerOccupancySnapshot
{
    private readonly Dictionary<string, (int MapId, int StationId)> _standing;
    private readonly List<(string VehicleKey, int MapId, int StationId)> _targets;

    internal ChargerOccupancySnapshot(
        IReadOnlyList<string> unknown,
        IReadOnlyDictionary<string, RiotVehicleObservation> vehicles,
        Dictionary<string, (int MapId, int StationId)> standing,
        List<(string VehicleKey, int MapId, int StationId)> targets)
    {
        Unknown = unknown;
        Vehicles = vehicles;
        _standing = standing;
        _targets = targets;
    }

    /// <summary>哪些事实没读到（例如 <c>AGV-2:VEHICLE_POSITION_STALE</c>、<c>ORDER_LISTING_INCOMPLETE</c>）；空即三样都读全了。</summary>
    public IReadOnlyList<string> Unknown { get; }

    /// <summary>读到了、且新鲜的车辆观测，按 <c>VehicleKey</c>。读不到或过期的车不在里面。</summary>
    public IReadOnlyDictionary<string, RiotVehicleObservation> Vehicles { get; }

    /// <summary>
    /// 这个桩此刻能不能确认空闲：能答空，否则答 <see cref="ChargingAllocationReasons"/> 里的那一条。
    /// </summary>
    /// <param name="askingVehicleKey">替哪辆车问：它自己停在这个桩上不算「被别的车占着」。为空即替谁也不是（释放预占前的核对）。</param>
    public string? Judge(int mapId, int stationId, string? askingVehicleKey)
    {
        if (Unknown.Count > 0)
        {
            return ChargingAllocationReasons.ChargerOccupancyUnknown;
        }

        if (_standing.Any(pair => pair.Value == (mapId, stationId) &&
                                  !string.Equals(pair.Key, askingVehicleKey, StringComparison.Ordinal)))
        {
            return ChargingAllocationReasons.ChargerOccupiedByVehicle;
        }

        return _targets.Any(target => target.MapId == mapId && target.StationId == stationId)
            ? ChargingAllocationReasons.ChargerTargetedByRunningOrder
            : null;
    }
}

/// <summary>读「桩是否被占」要用的 RIoT 事实（<c>REQ-0170</c>；批次9-06，control-server#404）。</summary>
/// <remarks>
/// <para>
/// <b>RIoT 没有「站点是否被占」这类查询</b>（调用白名单 1.1 节），所以用已列的三条只读查询拼：
/// </para>
/// <list type="number">
/// <item><c>getVehicleInfoByDeviceKey</c>（<see cref="IRiotVehicleFacts.ReadVehicleAsync"/>）：本项目每辆车的当前地图与当前站——
/// 有没有车停在桩上。</item>
/// <item><c>orderRecord</c> 按状态的订单清单（<see cref="IRiotOrderListingFacts.ListUnfinishedOrdersAsync"/>，状态 1、3、7、9）：
/// 本项目车辆身上还没结束的订单。</item>
/// <item><c>detailByUpperId</c>（<see cref="IRiotOrderMissionFacts.ReadOrderMissionFactsAsync"/>）：上面那些订单里不是本服务端建的，
/// 目的站是哪一个。本服务端建的单不问 RIoT，目的站取它冻结的订单意图。</item>
/// </list>
/// <para>
/// <b>只看本项目车辆</b>（车队名册 <see cref="VehicleRoster"/> 里的车；票面第 3.3 条，依据是用户 2026-09-04 确认不会有其它项目的车占用 8005 的桩）。
/// 清单里指定车与执行车都不是本项目车辆的订单不读、不算。
/// </para>
/// <para>
/// <b>未知即不分配</b>（fail-closed）：某辆车的位置读不到、读数过期（超过 <c>JourneyRuntime:MaximumEvidenceAge</c>，或时刻在未来）、
/// 当前站为空；清单不完整；清单里本项目车辆的某张单没有 <c>upperId</c>、或按 <c>upperId</c> 读不到它——任何一条，这一轮所有桩都不是候选。
/// </para>
/// </remarks>
public sealed class ChargerOccupancyReader(
    ControlServerDbContext dbContext,
    IRiotVehicleFacts vehicleFacts,
    IRiotOrderListingFacts orderListing,
    IRiotOrderMissionFacts orderMissions,
    VehicleRoster fleet,
    IOptions<JourneyRuntimeOptions> runtimeOptions,
    TimeProvider timeProvider)
{
    private readonly JourneyRuntimeOptions _runtime = runtimeOptions.Value;

    public async Task<ChargerOccupancySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        List<string> unknown = [];
        Dictionary<string, RiotVehicleObservation> vehicles = new(StringComparer.Ordinal);
        Dictionary<string, (int MapId, int StationId)> standing = new(StringComparer.Ordinal);
        foreach (FleetVehicle vehicle in fleet.Vehicles)
        {
            RiotVehicleObservation observed;
            try
            {
                observed = await vehicleFacts.ReadVehicleAsync(vehicle.VehicleKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                          !cancellationToken.IsCancellationRequested)
            {
                unknown.Add($"{vehicle.AgvId}:VEHICLE_POSITION_UNREADABLE");
                continue;
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            if (observed.ObservedAt > now || now - observed.ObservedAt > _runtime.MaximumEvidenceAge)
            {
                unknown.Add($"{vehicle.AgvId}:VEHICLE_POSITION_STALE");
                continue;
            }

            if (observed.CurrentStationId is not int station)
            {
                unknown.Add($"{vehicle.AgvId}:VEHICLE_POSITION_MISSING");
                continue;
            }

            vehicles[vehicle.VehicleKey] = observed;
            // A vehicle RIoT places on another Map stands on none of this Map's chargers. One with no Map reported is taken to
            // stand where its station number says, on this Map: the side that keeps a charger from being handed out.
            if (string.IsNullOrEmpty(observed.CurrentMap) ||
                string.Equals(observed.CurrentMap, _runtime.MapIdentity, StringComparison.Ordinal))
            {
                standing[vehicle.VehicleKey] = (_runtime.MapId, station);
            }
        }

        List<(string VehicleKey, int MapId, int StationId)> targets = [];
        RiotUnfinishedOrderListing listing = await orderListing.ListUnfinishedOrdersAsync(cancellationToken).ConfigureAwait(false);
        if (!listing.IsComplete)
        {
            unknown.Add("ORDER_LISTING_INCOMPLETE");
            return new ChargerOccupancySnapshot(unknown, vehicles, standing, targets);
        }

        HashSet<string> ours = fleet.Vehicles.Select(vehicle => vehicle.VehicleKey).ToHashSet(StringComparer.Ordinal);
        (RiotListedOrder Order, string VehicleKey)[] onOurVehicles =
        [
            .. listing.Orders
                .Select(order => (
                    Order: order,
                    VehicleKey: order.ExecuteVehicleKey is { } executing && ours.Contains(executing) ? executing
                        : order.AppointVehicleKey is { } appointed && ours.Contains(appointed) ? appointed
                        : null))
                .Where(item => item.VehicleKey is not null)
                .Select(item => (item.Order, item.VehicleKey!)),
        ];
        if (onOurVehicles.Length == 0)
        {
            return new ChargerOccupancySnapshot(unknown, vehicles, standing, targets);
        }

        string[] upperIds = [.. onOurVehicles.Select(item => item.Order.UpperId).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!)];
        var ownIntents = await dbContext.OrderIntents.AsNoTracking()
            .Where(row => upperIds.Contains(row.UpperId))
            .Select(row => new { row.UpperId, row.MapId, row.DestinationStationId })
            .ToDictionaryAsync(row => row.UpperId, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        foreach ((RiotListedOrder order, string vehicleKey) in onOurVehicles)
        {
            if (string.IsNullOrWhiteSpace(order.UpperId))
            {
                unknown.Add($"ORDER_{order.OrderId}:NO_UPPER_ID");
                continue;
            }

            if (ownIntents.TryGetValue(order.UpperId, out var intent))
            {
                targets.Add((vehicleKey, intent.MapId, intent.DestinationStationId));
                continue;
            }

            RiotOrderMissionFacts facts = await orderMissions
                .ReadOrderMissionFactsAsync(order.UpperId, cancellationToken).ConfigureAwait(false);
            if (facts.Status != RiotOrderMissionFactsStatus.Found)
            {
                unknown.Add($"ORDER_{order.OrderId}:DESTINATION_UNREADABLE");
                continue;
            }

            // Every move of the order, not only the last: a vehicle passing through a charger on the way somewhere else is
            // heading for it too, for as long as that order runs.
            targets.AddRange(facts.Missions
                .Where(mission => string.Equals(mission.Type, "move", StringComparison.OrdinalIgnoreCase) &&
                                  mission.Destination is > 0)
                .Select(mission => (vehicleKey, mission.MapId is > 0 ? mission.MapId.Value : _runtime.MapId, mission.Destination!.Value)));
        }

        return new ChargerOccupancySnapshot(unknown, vehicles, standing, targets);
    }

    /// <summary>
    /// RIoT 的未完成订单清单读全了、且里面没有一张指定给这辆车或由它执行的单。清单不完整答假——不知道就不算没有。
    /// </summary>
    /// <remarks>
    /// 引擎放弃一张「发出过、RIoT 一直查无此单」的充电单之前核这一条（独立审查 M2(b)）：按 <c>upperId</c> 查不到，不等于这辆车身上没有单。
    /// </remarks>
    public async Task<bool> VehicleHasNoUnfinishedOrderAsync(string vehicleKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        RiotUnfinishedOrderListing listing = await orderListing.ListUnfinishedOrdersAsync(cancellationToken).ConfigureAwait(false);
        return listing.IsComplete &&
               !listing.Orders.Any(order =>
                   string.Equals(order.ExecuteVehicleKey, vehicleKey, StringComparison.Ordinal) ||
                   string.Equals(order.AppointVehicleKey, vehicleKey, StringComparison.Ordinal));
    }
}
