using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 车队视图「充电暂停与等待」的数据面（批次9-10，control-server#408；REQ-0171、REQ-0178、REQ-0179、REQ-0285）：车辆充电资格暂停、服务端持有的
/// <c>ManualChargingHold</c>、清桩中的车，以及最近的清桩记录。桩的分配暂停在「充电桩」卡片上，跟着桩显示。
/// </summary>
/// <remarks>
/// <para>
/// 人工充电等待、清桩、资格暂停都是服务端的记录，不是车辆的读数：车辆失联时照样列出、标出失联（<c>vehicleInContact = false</c>），
/// 与站点独占同一个口径——失联的车不会因此从「等人工充电」或「清桩中」的名单上悄悄消失。
/// </para>
/// <para>
/// 只读：维修暂停、恢复确认、人工清桩都走 control-server#406 的 Host 接口，这里只把路径写出来（<c>hostEntries</c>）。
/// </para>
/// </remarks>
internal sealed class ChargingHoldsQueryEndpoint : IDashboardQueryEndpoint
{
    private readonly VehicleRoster _roster;
    private readonly TimeProvider _clock;

    public ChargingHoldsQueryEndpoint()
        : this(new VehicleRoster(Options.Create(new JourneyRuntimeOptions())), TimeProvider.System)
    {
    }

    /// <summary>挂在宿主上时用这一个：名册从宿主的同一份配置建，不要求宿主另外注册名册。</summary>
    [ActivatorUtilitiesConstructor]
    public ChargingHoldsQueryEndpoint(IOptions<JourneyRuntimeOptions> options)
        : this(new VehicleRoster(options), TimeProvider.System)
    {
    }

    internal ChargingHoldsQueryEndpoint(VehicleRoster roster, TimeProvider clock)
    {
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "charging-holds";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ChargingDashboardFacts facts =
            await ChargingDashboardFacts.ReadAsync(dbContext, _roster, _clock.GetUtcNow(), cancellationToken);
        return new
        {
            eligibilityHoldsNote = ChargingDashboardDescriptions.InterruptionNotImplemented,
            eligibilityHolds = facts.OpenEligibilityHolds
                .Select(hold => new
                {
                    holdId = hold.HoldId,
                    agvId = facts.AgvIdOf(hold.VehicleKey),
                    vehicleInContact = facts.InContact(hold.VehicleKey),
                    reason = hold.Reason,
                    reasonDescription = ChargingDashboardDescriptions.EligibilityHoldReasons.GetValueOrDefault(hold.Reason),
                    heldAt = hold.HeldAt,
                    cycleId = hold.CycleId,
                    evidenceReference = hold.EvidenceReference,
                })
                .ToArray(),
            manualChargingHolds = facts.ManualHolds
                .Select(hold => new
                {
                    holdId = hold.HoldId,
                    agvId = facts.AgvIdOf(hold.VehicleKey),
                    vehicleKey = hold.VehicleKey,
                    vehicleInContact = facts.InContact(hold.VehicleKey),
                    reason = hold.Reason,
                    reasonDescription = ChargingDashboardDescriptions.ManualHoldReasons.GetValueOrDefault(hold.Reason),
                    since = hold.Since,
                    warnedAt = hold.WarnedAt,
                    releaseHint = ChargingDashboardDescriptions.ManualHoldReleaseHint,
                })
                .ToArray(),
            clearing = facts.OpenCycles
                .Where(cycle => cycle.Phase == Application.ChargingCyclePhases.Clearing)
                .OrderBy(cycle => facts.AgvIdOf(cycle.VehicleKey), StringComparer.Ordinal)
                .Select(cycle =>
                {
                    JourneyRuntimeRow? journey = facts.OpenChargingJourneys.GetValueOrDefault(cycle.JourneyId);
                    StationClearanceRow? clearance = facts.OpenClearances.GetValueOrDefault(cycle.CycleId);
                    return new
                    {
                        agvId = facts.AgvIdOf(cycle.VehicleKey),
                        vehicleInContact = facts.InContact(cycle.VehicleKey),
                        cycleId = cycle.CycleId,
                        journeyId = cycle.JourneyId,
                        mapId = cycle.MapId,
                        stationId = cycle.StationId,
                        stationName = facts.StationNameOf(cycle.ChargerRosterVersion, cycle.MapId, cycle.StationId),
                        guidance = "车保持原位，服务端不为它建单、不动车；等 R-11／R-13 名单里的人到现场确认清桩，并在 RIoT 里结束旧充电单",
                        journeyCode = journey?.BlockReasonCode,
                        journeyCodeDescription = ChargingDashboardDescriptions.DescribeChargingCode(journey?.BlockReasonCode),
                        journeyCodeSince = journey?.BlockReasonCode is null ? null : journey.BlockReasonSince,
                        clearance = clearance is null ? null : facts.Clearance(clearance),
                    };
                })
                .ToArray(),
            recentClearances = facts.RecentClearances.Select(facts.Clearance).ToArray(),
            hostEntries = ChargingDashboardDescriptions.HostEntries,
            unavailableVehicles = facts.Contact.Unavailable(),
        };
    }
}
