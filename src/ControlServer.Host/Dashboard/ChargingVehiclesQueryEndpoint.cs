using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 车队视图「逐车充电状态」的数据面（批次9-10，control-server#408）：每台投运车辆此刻的用途、<c>chargingCycleState</c>、周期阶段、目标桩、
/// 周期所依据的名册与策略版本、充电旅程上的码，以及它是否在人工充电等待、车辆充电资格暂停或清桩中。
/// </summary>
/// <remarks>
/// <para>
/// <b>电量、batteryState 与排队原因这三格读不到</b>，照实写（调度 10-02 已报）：每辆车此刻的电量与 batteryState 没有落库，只在每轮派车时从
/// RIoT 现读；分不到桩的原因只记在内存单例 <c>ChargingAllocationBoard</c> 的「最近一次」上，没有轮次、没有时刻，原样给就是 REQ-0269 禁止的
/// 不确定新旧的旧值。看板进程不另算这些事实。
/// </para>
/// <para>
/// 听不到的车只进 <c>unavailableVehicles</c>（只有车号与原因），不给它失联前的用途、周期或码（REQ-0269，与车辆用途卡片同一个判定）。
/// 它被持有的桩、它的人工充电等待与清桩是服务端的记录，照样出现在充电桩与暂停卡片上并标出失联。
/// </para>
/// </remarks>
internal sealed class ChargingVehiclesQueryEndpoint : IDashboardQueryEndpoint
{
    private readonly VehicleRoster _roster;
    private readonly TimeProvider _clock;

    public ChargingVehiclesQueryEndpoint()
        : this(new VehicleRoster(Options.Create(new JourneyRuntimeOptions())), TimeProvider.System)
    {
    }

    /// <summary>挂在宿主上时用这一个：名册从宿主的同一份配置建，不要求宿主另外注册名册。</summary>
    [ActivatorUtilitiesConstructor]
    public ChargingVehiclesQueryEndpoint(IOptions<JourneyRuntimeOptions> options)
        : this(new VehicleRoster(options), TimeProvider.System)
    {
    }

    internal ChargingVehiclesQueryEndpoint(VehicleRoster roster, TimeProvider clock)
    {
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "charging-vehicles";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ChargingDashboardFacts facts =
            await ChargingDashboardFacts.ReadAsync(dbContext, _roster, _clock.GetUtcNow(), cancellationToken);
        return new
        {
            batteryNotReadable = ChargingDashboardDescriptions.BatteryNotReadable,
            queueReasonNotReadable = ChargingDashboardDescriptions.QueueReasonNotReadable,
            vehicles = facts.Contact.Vehicles
                .Where(facts.Contact.InContact)
                .Select(vehicle => Vehicle(facts, vehicle))
                .ToArray(),
            unavailableVehicles = facts.Contact.Unavailable(),
        };
    }

    private static object Vehicle(ChargingDashboardFacts facts, FleetVehicle vehicle)
    {
        VehiclePurposeClaimRow? claim = facts.Claims.GetValueOrDefault(vehicle.VehicleKey);
        ChargingCycleRow? cycle = facts.OpenCycles.SingleOrDefault(row => row.VehicleKey == vehicle.VehicleKey);
        JourneyRuntimeRow? journey = cycle is null ? null : facts.OpenChargingJourneys.GetValueOrDefault(cycle.JourneyId);
        string state = cycle?.WireState ?? ChargingCycleWireStates.NotCharging;
        ManualChargingHoldRow? manual = facts.ManualHolds.SingleOrDefault(row => row.VehicleKey == vehicle.VehicleKey);
        return new
        {
            agvId = vehicle.AgvId,
            vehicleKey = vehicle.VehicleKey,
            purpose = claim?.Purpose,
            purposeDescription = claim is null
                ? "没有任何用途占着这辆车"
                : DashboardDescriptions.Purposes.GetValueOrDefault(claim.Purpose) ?? "服务端记下的用途没有中文说明，请报开发",
            holderJourneyId = claim?.JourneyId,
            holderKind = claim is null ? null : VehiclePurposeFacts.HolderKind(claim.JourneyId, claim.Purpose),
            holderKindDescription = claim is null ? null : VehiclePurposeFacts.HolderKindDescription(claim.JourneyId, claim.Purpose),
            chargingCycleState = state,
            chargingCycleStateDescription = ChargingDashboardDescriptions.CycleStates.GetValueOrDefault(state)
                                            ?? "服务端记下的状态没有中文说明，请报开发",
            cycle = cycle is null
                ? null
                : new
                {
                    cycleId = cycle.CycleId,
                    journeyId = cycle.JourneyId,
                    phase = cycle.Phase,
                    phaseDescription = ChargingDashboardDescriptions.CyclePhases.GetValueOrDefault(cycle.Phase),
                    targetMapId = cycle.MapId,
                    targetStationId = cycle.StationId,
                    targetStationName = facts.StationNameOf(cycle.ChargerRosterVersion, cycle.MapId, cycle.StationId),
                    chargerRosterVersion = cycle.ChargerRosterVersion,
                    chargingPolicyVersion = cycle.ChargingPolicyVersion,
                    allocatedAt = cycle.AllocatedAt,
                    orderConfirmedAt = cycle.OrderConfirmedAt,
                    arrivedAt = cycle.ArrivedAt,
                    firstChargingSeenAt = cycle.FirstChargingSeenAt,
                    completedAt = cycle.CompletedAt,
                },
            journeyCode = journey?.BlockReasonCode,
            journeyCodeDescription = ChargingDashboardDescriptions.DescribeChargingCode(journey?.BlockReasonCode),
            journeyCodeSince = journey?.BlockReasonCode is null ? null : journey.BlockReasonSince,
            manualChargingHold = manual is null
                ? null
                : new
                {
                    reason = manual.Reason,
                    reasonDescription = ChargingDashboardDescriptions.ManualHoldReasons.GetValueOrDefault(manual.Reason),
                    since = manual.Since,
                },
            eligibilityHeld = facts.OpenEligibilityHolds.Any(row => row.VehicleKey == vehicle.VehicleKey),
        };
    }
}
