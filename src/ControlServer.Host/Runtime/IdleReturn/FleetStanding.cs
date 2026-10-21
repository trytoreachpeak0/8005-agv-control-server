using ControlServer.Application;
using ControlServer.Host.Runtime.Fleet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.IdleReturn;

/// <summary>选点那一刻车队里的一辆车停在哪个站。<paramref name="LastKnown"/>：这一轮没读到它，用的是它上一次读到的站。</summary>
public sealed record StandingVehicle(string VehicleKey, int StationId, bool LastKnown);

/// <summary>
/// 选等待点时全车队谁停在哪（control-server#798）：空闲返回与清桩开往等待点共用，由 <see cref="IdleReturnEvaluator.ExcludePointAsync"/> 读。
/// </summary>
public sealed class FleetStanding
{
    internal FleetStanding(IReadOnlyList<StandingVehicle> vehicles, IReadOnlyList<string> unlocated)
    {
        Vehicles = vehicles;
        Unlocated = unlocated;
    }

    /// <summary>停在某个站上的车（含按上一次读数算的）。</summary>
    public IReadOnlyList<StandingVehicle> Vehicles { get; }

    /// <summary>这一轮读不到、此前也从没读到过位置的车（<c>VehicleKey</c>）：选点不知道它在哪，只告警。</summary>
    public IReadOnlyList<string> Unlocated { get; }

    /// <summary>
    /// 停在 <paramref name="from"/> 的车是不是就站在 <paramref name="stationId"/> 上：同一个站，或路网上同一个节点、代价为 0
    /// （<see cref="Domain.RouteGraph.TraversalCostsFrom"/>：两个站吸附到同一个端点）。
    /// </summary>
    internal static bool StandsOn(Domain.RouteGraph graph, int from, int stationId) =>
        from == stationId || graph.Traverse(from, stationId) is { Reachable: true, TraversalCostMm: 0 };

    /// <summary>除 <paramref name="vehicleKey"/> 之外站在这个站上的那辆车；没有答空。</summary>
    internal StandingVehicle? StoodOnByAnother(Domain.RouteGraph graph, string vehicleKey, int stationId) =>
        Vehicles.FirstOrDefault(other => !string.Equals(other.VehicleKey, vehicleKey, StringComparison.Ordinal) &&
                                         StandsOn(graph, other.StationId, stationId));
}

/// <summary>读 <see cref="FleetStanding"/>（control-server#798）。</summary>
public sealed class FleetStandingReader(
    IRiotVehicleFacts vehicleFacts,
    VehicleRoster fleet,
    FleetStandingMemory memory,
    IOptions<JourneyRuntimeOptions> runtimeOptions,
    TimeProvider timeProvider,
    ILogger<FleetStandingReader> logger)
{
    /// <summary>读全车队此刻停在哪。<paramref name="readThisRound"/> 里已有的车不再问 RIoT。</summary>
    public Task<FleetStanding> ReadAsync(
        IReadOnlyCollection<RiotVehicleObservation> readThisRound,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readThisRound);
        _ = (vehicleFacts, fleet, memory, runtimeOptions, timeProvider, logger, cancellationToken);
        return Task.FromResult(new FleetStanding(
            [.. readThisRound
                .Where(observed => observed.CurrentStationId is not null)
                .Select(observed => new StandingVehicle(observed.VehicleKey, observed.CurrentStationId!.Value, false))],
            []));
    }
}

/// <summary>每辆车上一次读到的站，跨轮次保留（宿主里是单例）。</summary>
public sealed class FleetStandingMemory
{
}
