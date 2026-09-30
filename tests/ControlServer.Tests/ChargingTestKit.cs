using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.RouteGraph;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlServer.Tests;

/// <summary>测试夹具组装派车轮时用的充电分配器（control-server#404）：派车轮要求必填，生产的组装同样总有一个。</summary>
internal static class ChargingTestKit
{
    /// <summary>
    /// 按宿主的组装建一个分配器。没有车低于强制充电线、也没有人工充电等待时，它不读时钟、不问 RIoT、不写库——既有用例的逐字转录因此不变。
    /// </summary>
    public static ChargingAllocator Create(
        ControlServerDbContext context,
        JourneyRuntimeOptions options,
        TimeProvider clock,
        IRiotVehicleFacts vehicleFacts,
        IOnboardPeer peer,
        IRiotOrderListingFacts? orderListing = null,
        IRiotOrderMissionFacts? orderMissions = null,
        RouteGraphAccess? routeGraph = null,
        ChargingAllocationBoard? board = null,
        ILogger<ChargingAllocator>? logger = null)
    {
        Microsoft.Extensions.Options.IOptions<JourneyRuntimeOptions> runtime = Microsoft.Extensions.Options.Options.Create(options);
        VehicleRoster fleet = new(runtime);
        QuietOrders quiet = new();
        return new ChargingAllocator(
            context,
            new VehiclePurposeLedgerStore(context),
            new StationExclusivityStore(context),
            new VehicleFaultStore(context),
            new ChargerRosterStore(context, JourneyRuntimeWorkerTestKit.CreateGovernedPublisher(context)),
            new ChargingCycleStore(context),
            new ChargingHoldStore(context),
            new ManualChargingHoldStore(context),
            new ChargerOccupancyReader(context, vehicleFacts, orderListing ?? quiet, orderMissions ?? quiet, fleet, runtime, clock),
            routeGraph ?? new RouteGraphAccess(
                new RouteGraphSnapshotStore(context),
                Microsoft.Extensions.Options.Options.Create(new RouteGraphOptions { MapId = options.MapId }),
                clock),
            new OnboardJourneyPublisher(new WireToGateStore(context), peer, clock),
            fleet,
            runtime,
            board ?? new ChargingAllocationBoard(),
            clock,
            logger ?? NullLogger<ChargingAllocator>.Instance);
    }

    /// <summary>一个没有任何未完成订单的 RIoT：清单完整且为空，按单号查不到任何 mission。答复里的时刻固定，不读夹具的时钟。</summary>
    private sealed class QuietOrders : IRiotOrderListingFacts, IRiotOrderMissionFacts
    {
        public Task<RiotUnfinishedOrderListing> ListUnfinishedOrdersAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new RiotUnfinishedOrderListing(true, [], DateTimeOffset.UnixEpoch));

        public Task<RiotOrderStateReading> ReadOrderStateAsync(string orderId, CancellationToken cancellationToken) =>
            Task.FromResult(new RiotOrderStateReading(orderId, null, DateTimeOffset.UnixEpoch));

        public Task<RiotOrderMissionFacts> ReadOrderMissionFactsAsync(string upperId, CancellationToken cancellationToken) =>
            Task.FromResult(new RiotOrderMissionFacts(
                upperId, RiotOrderMissionFactsStatus.Unknown, null, null, [], DateTimeOffset.UnixEpoch));
    }
}
