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

    /// <summary>
    /// 「点上有车」这一格的判法，空闲返回（等待点与待送固定起点）与清桩开往等待点共用这一处：另一辆车站在这个站上答
    /// <see cref="IdleReturnReasons.PointStoodOn"/>，否则答空。
    /// </summary>
    internal string? Excludes(Domain.RouteGraph graph, string vehicleKey, int stationId) =>
        StoodOnByAnother(graph, vehicleKey, stationId) is null ? null : IdleReturnReasons.PointStoodOn;
}

/// <summary>读 <see cref="FleetStanding"/>（control-server#798）。</summary>
/// <remarks>
/// <para>
/// <b>全车队</b>：车队名册（<see cref="VehicleRoster"/>）里的每一辆车，不论它此刻空闲、在跑旅程、被外来订单占着还是离线——停在点上的车不会因为
/// 它这一轮不被评估就不在那里。名册外的车（别的项目的车）不算，与充电桩占用同一个口径（<see cref="Charging.ChargerOccupancyReader"/>）。
/// </para>
/// <para>
/// <b>怎么读一辆车</b>，与充电桩占用同一套：这一轮已经为它读过的读数直接用，其余问 RIoT（<c>getVehicleInfoByDeviceKey</c>）。离线的车按 RIoT
/// 报的最后位置算；不在本图的车、报 <c>currentPosition</c> 为 0（在两站之间，<see cref="Charging.ChargerOccupancyReader.NoStation"/>）的车不站在任何点上。
/// </para>
/// <para>
/// <b>读不到位置时</b>（RIoT 这一次没答或在这辆车自己的派车预算内没答完、读数过期、没有当前站）偏保守：按这辆车上一次读到的站算它还站在那里
/// （<see cref="FleetStandingMemory"/>，服务端进程内跨轮次保留；每轮派车读到的车与每次选点读到的车都记进去，<see cref="Remember"/>）。
/// 启动之后一次都没读到过的车不知道在哪，不挡任何点，但告警（事件 2338），连着读不到只记一次——不静默当作不占点，也不因为一辆车读不到就让全车队的选点
/// 都停下（清桩停下就是一直占着桩）。
/// </para>
/// </remarks>
public sealed class FleetStandingReader(
    IRiotVehicleFacts vehicleFacts,
    VehicleRoster fleet,
    FleetStandingMemory memory,
    IOptions<JourneyRuntimeOptions> runtimeOptions,
    TimeProvider timeProvider,
    ILogger<FleetStandingReader> logger)
{
    private static readonly Action<ILogger, string, string, string, Exception?> LogUnlocated =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2338, nameof(LogUnlocated)),
            "Waiting point selection cannot place vehicle {AgvId} [{VehicleKey}] ({Why}) and has not read where it stands since the " +
            "server started: points are chosen as if it stood on none, so another vehicle may be sent to the point it is on. Said " +
            "once until it is read again.");

    private readonly JourneyRuntimeOptions _runtime = runtimeOptions.Value;

    /// <summary>读全车队此刻停在哪。<paramref name="readThisRound"/> 里已有的车不再问 RIoT。</summary>
    public async Task<FleetStanding> ReadAsync(
        IReadOnlyCollection<RiotVehicleObservation> readThisRound,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readThisRound);
        Dictionary<string, RiotVehicleObservation> read = new(StringComparer.Ordinal);
        foreach (RiotVehicleObservation observed in readThisRound)
        {
            read.TryAdd(observed.VehicleKey, observed);
        }

        List<StandingVehicle> standing = [];
        List<string> unlocated = [];
        foreach (FleetVehicle vehicle in fleet.Vehicles)
        {
            string key = vehicle.VehicleKey;
            string? why;
            if (!read.TryGetValue(key, out RiotVehicleObservation? observed))
            {
                observed = await ReadWithinBudgetAsync(vehicle, cancellationToken).ConfigureAwait(false);
            }

            (bool located, int? station, why) = observed is null ? (false, null, "unreadable") : Locate(observed);
            if (located)
            {
                memory.Saw(key, station);
                if (station is int on)
                {
                    standing.Add(new StandingVehicle(key, on, LastKnown: !observed!.Connected));
                }
                continue;
            }

            if (memory.LastSeen(key) is int last)
            {
                standing.Add(new StandingVehicle(key, last, LastKnown: true));
                continue;
            }

            unlocated.Add(key);
            if (memory.FirstUnlocated(key))
            {
                LogUnlocated(logger, vehicle.AgvId, key, why ?? "unlocated", null);
            }
        }

        return new FleetStanding(standing, unlocated);
    }

    /// <summary>
    /// 记下这些读数里每辆车停在哪（派车轮每轮为参加的车读的那一份），给之后读不到它的选点用。读数过期、没有当前站的不记。
    /// </summary>
    public void Remember(IEnumerable<RiotVehicleObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        foreach (RiotVehicleObservation observed in observations)
        {
            if (Locate(observed) is (true, var station, _))
            {
                memory.Saw(observed.VehicleKey, station);
            }
        }
    }

    /// <summary>
    /// 一份读数说没说清车在哪：说清了答真，站为空即不在本图的任何站上（在两站之间、在别的图上）；没说清答假与为什么。
    /// </summary>
    private (bool Located, int? Station, string? Why) Locate(RiotVehicleObservation observed)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (observed.ObservedAt > now || now - observed.ObservedAt > _runtime.MaximumEvidenceAge)
        {
            return (false, null, "stale reading");
        }

        // On another Map it stands on none of this Map's points, whatever its station number says.
        if (!string.IsNullOrEmpty(observed.CurrentMap) &&
            !string.Equals(observed.CurrentMap, _runtime.MapIdentity, StringComparison.Ordinal))
        {
            return (true, null, null);
        }

        return observed.CurrentStationId switch
        {
            null => (false, null, "no current station"),
            // Between stations (control-server#772): on no point now, and the station it was last seen on is behind it.
            <= Charging.ChargerOccupancyReader.NoStation => (true, null, null),
            int station => (true, station, null),
        };
    }

    /// <summary>
    /// 问 RIoT 这辆车在哪，用它自己的派车预算（<see cref="FleetVehicleOptions.RoundTimeoutMilliseconds"/>）截断：一辆挂住的车不拖住整次选点。
    /// 读不到答空。
    /// </summary>
    private async Task<RiotVehicleObservation?> ReadWithinBudgetAsync(FleetVehicle vehicle, CancellationToken cancellationToken)
    {
        int milliseconds = VehicleDispatchPolicyAccess.ConfiguredFleet(_runtime)
            .FirstOrDefault(entry => string.Equals(entry.AgvId, vehicle.AgvId, StringComparison.Ordinal))?.RoundTimeoutMilliseconds ?? 0;
        using CancellationTokenSource expiry = new(
            TimeSpan.FromMilliseconds(milliseconds > 0 ? milliseconds : new FleetVehicleOptions().RoundTimeoutMilliseconds), timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        try
        {
            return await vehicleFacts.ReadVehicleAsync(vehicle.VehicleKey, linked.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or OperationCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}

/// <summary>
/// 每辆车上一次读到的站，跨轮次保留（宿主里是单例，control-server#798）：这一轮读不到位置的车按它算。服务端重启即清空。
/// </summary>
public sealed class FleetStandingMemory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _lastSeen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    /// <summary>读到了这辆车的位置：停在 <paramref name="station"/> 上，或为空即不在任何站上。之后再读不到时重新告警。</summary>
    internal void Saw(string vehicleKey, int? station)
    {
        lock (_gate)
        {
            if (station is int seen)
            {
                _lastSeen[vehicleKey] = seen;
            }
            else
            {
                _lastSeen.Remove(vehicleKey);
            }
            _warned.Remove(vehicleKey);
        }
    }

    /// <summary>这辆车上一次读到时停的站；没读到过、或上一次读到时不在任何站上，为空。</summary>
    internal int? LastSeen(string vehicleKey)
    {
        lock (_gate)
        {
            return _lastSeen.TryGetValue(vehicleKey, out int station) ? station : null;
        }
    }

    /// <summary>这辆车这一段连着读不到的第一次：答真，之后答假，直到它又被读到。</summary>
    internal bool FirstUnlocated(string vehicleKey)
    {
        lock (_gate)
        {
            return _warned.Add(vehicleKey);
        }
    }
}
