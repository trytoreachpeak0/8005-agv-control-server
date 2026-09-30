using ControlServer.Application;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.RouteGraph;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Charging;

/// <summary>派车轮交给充电分配的一辆空闲车，连同这一轮为它读的动态事实。</summary>
public sealed record ChargingCandidate(FleetVehicle Vehicle, DispatchVehicleFacts Facts);

/// <summary>一辆车这一轮的充电分配结论。形成承诺时带着所选的桩与承诺的旅程 id。</summary>
public sealed record ChargingAllocationVerdict(
    string AgvId,
    string VehicleKey,
    string Reason,
    string Detail = "",
    int? StationId = null,
    string? JourneyId = null)
{
    public bool Committed => Reason == ChargingAllocationReasons.Committed;
}

/// <summary>每辆车最近一次的充电分配结论，跨轮次保留（宿主里是单例）。</summary>
public sealed class ChargingAllocationBoard
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Reason, string Detail)> _last =
        new(StringComparer.Ordinal);

    /// <summary>记下这辆车这一轮的结论；与上一次不同（或第一次）时答真。</summary>
    public bool Record(string agvId, string reason, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        (string, string) now = (reason, detail);
        bool changed = !_last.TryGetValue(agvId, out (string Reason, string Detail) before) || before != now;
        _last[agvId] = now;
        return changed;
    }

    /// <summary>每辆车最近一次的原因码与细节。</summary>
    public IReadOnlyDictionary<string, (string Reason, string Detail)> Verdicts =>
        _last.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
}

/// <summary>充电分配、排队与原子承诺（批次9-06，control-server#404）。</summary>
public sealed class ChargingAllocator(
    ControlServerDbContext dbContext,
    IVehiclePurposeLedger ledger,
    IStationExclusivityStore stations,
    IVehicleFaultStore faults,
    IChargerRoster roster,
    IChargingCycleStore cycles,
    IChargingHoldStore holds,
    IManualChargingHoldStore manualHolds,
    ChargerOccupancyReader occupancy,
    RouteGraphAccess routeGraph,
    OnboardJourneyPublisher publisher,
    VehicleRoster fleet,
    IOptions<JourneyRuntimeOptions> runtimeOptions,
    ChargingAllocationBoard board,
    TimeProvider timeProvider,
    ILogger<ChargingAllocator> logger)
{
    public Task<IReadOnlyList<ChargingAllocationVerdict>> AllocateAsync(
        RiotMapStationCatalogSnapshot currentMap,
        IReadOnlyList<ChargingCandidate> candidates,
        CancellationToken cancellationToken)
    {
        _ = (dbContext, ledger, stations, faults, roster, cycles, holds, manualHolds, occupancy, routeGraph, publisher, fleet,
            runtimeOptions, board, timeProvider, logger, currentMap, candidates, cancellationToken);
        return Task.FromResult<IReadOnlyList<ChargingAllocationVerdict>>([]);
    }
}
