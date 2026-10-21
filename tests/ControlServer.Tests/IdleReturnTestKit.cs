using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Host.Runtime.RouteGraph;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlServer.Tests;

/// <summary>测试夹具组装派车轮时用的空闲返回评估器（control-server#389）：派车轮要求必填，生产的组装同样总有一个。</summary>
internal static class IdleReturnTestKit
{
    /// <summary>按宿主的组装建一个评估器；<paramref name="enabled"/> 为假（默认）时它什么也不读、不写。</summary>
    public static IdleReturnEvaluator Create(
        ControlServerDbContext context,
        JourneyRuntimeOptions options,
        TimeProvider clock,
        RouteGraphAccess? routeGraph = null,
        bool enabled = false,
        IdleReturnVerdictBoard? board = null,
        ILogger<IdleReturnEvaluator>? logger = null,
        IChargingPolicyResolver? chargingPolicy = null,
        IRiotVehicleFacts? vehicleFacts = null,
        FleetStandingMemory? standingMemory = null) =>
        new(
            context,
            new VehiclePurposeLedgerStore(context),
            new StationExclusivityStore(context),
            new VehicleFaultStore(context),
            // control-server#400: every vehicle has the approved test policy unless a test says otherwise.
            chargingPolicy ?? TestChargingPolicies.AllApproved,
            new WaitingPointRegistry(context, JourneyRuntimeWorkerTestKit.CreateGovernedPublisher(context)),
            TaskTypeStationRuntimeSeed.Access(context).Bindings,
            routeGraph ?? new RouteGraphAccess(
                new RouteGraphSnapshotStore(context),
                Microsoft.Extensions.Options.Options.Create(new RouteGraphOptions { MapId = options.MapId }),
                clock),
            new PolicyMandatoryChargeLine(chargingPolicy ?? TestChargingPolicies.AllApproved),
            Microsoft.Extensions.Options.Options.Create(new IdleReturnOptions { Enabled = enabled }),
            Microsoft.Extensions.Options.Options.Create(options),
            board ?? new IdleReturnVerdictBoard(),
            StandingReader(options, clock, vehicleFacts ?? new UnreadableVehicleFacts(), standingMemory),
            clock,
            logger ?? NullLogger<IdleReturnEvaluator>.Instance);

    /// <summary>选点用的全车队位置读取器（control-server#798），名册取 <paramref name="options"/> 的车队。</summary>
    public static FleetStandingReader StandingReader(
        JourneyRuntimeOptions options,
        TimeProvider clock,
        IRiotVehicleFacts vehicleFacts,
        FleetStandingMemory? memory = null,
        ILogger<FleetStandingReader>? logger = null) =>
        new(
            vehicleFacts,
            new VehicleRoster(Microsoft.Extensions.Options.Options.Create(options)),
            memory ?? new FleetStandingMemory(),
            Microsoft.Extensions.Options.Options.Create(options),
            clock,
            logger ?? NullLogger<FleetStandingReader>.Instance);

    /// <summary>不给车辆读数的夹具用：每辆车都读不到，选点只看交来的车。</summary>
    private sealed class UnreadableVehicleFacts : IRiotVehicleFacts
    {
        public Task<RiotVehicleObservation> ReadVehicleAsync(string vehicleKey, CancellationToken cancellationToken) =>
            throw new HttpRequestException("This fixture reads no vehicle.");

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Waiting point selection reads no order.");

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Waiting point selection creates no order.");
    }
}
