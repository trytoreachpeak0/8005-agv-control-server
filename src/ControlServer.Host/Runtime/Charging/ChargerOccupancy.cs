using ControlServer.Application;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Charging;

/// <summary>这一轮读到的占用事实；桩的结论由 <see cref="Judge"/> 给。</summary>
public sealed class ChargerOccupancySnapshot
{
    /// <summary>这个桩此刻能不能确认空闲：能答空，否则答 <see cref="ChargingAllocationReasons"/> 里的那一条。</summary>
    public string? Judge(int mapId, int stationId, string? askingVehicleKey) => throw new NotImplementedException();
}

/// <summary>读「桩是否被占」要用的 RIoT 事实（批次9-06，control-server#404）。</summary>
public sealed class ChargerOccupancyReader(
    ControlServerDbContext dbContext,
    IRiotVehicleFacts vehicleFacts,
    IRiotOrderListingFacts orderListing,
    IRiotOrderMissionFacts orderMissions,
    VehicleRoster fleet,
    IOptions<JourneyRuntimeOptions> runtimeOptions,
    TimeProvider timeProvider)
{
    public Task<ChargerOccupancySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        _ = (dbContext, vehicleFacts, orderListing, orderMissions, fleet, runtimeOptions, timeProvider, cancellationToken);
        throw new NotImplementedException();
    }
}
