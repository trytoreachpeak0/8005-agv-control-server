using ControlServer.Application;
using ControlServer.Host.Runtime;
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
        IChargingPolicyResolver? chargingPolicy = null) =>
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
            new TransitionalMandatoryChargeLine(Microsoft.Extensions.Options.Options.Create(options)),
            Microsoft.Extensions.Options.Options.Create(new IdleReturnOptions { Enabled = enabled }),
            Microsoft.Extensions.Options.Options.Create(options),
            board ?? new IdleReturnVerdictBoard(),
            clock,
            logger ?? NullLogger<IdleReturnEvaluator>.Instance);
}
