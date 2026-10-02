using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 车队视图「逐车充电状态」的数据面（批次9-10，control-server#408）：每台投运车辆此刻的电量与读数来源、batteryState 投影、用途、
/// <c>chargingCycleState</c>、周期阶段、目标桩、周期所依据的名册与策略版本、充电旅程上的码、排队原因，以及它是否在人工充电等待或资格暂停。
/// </summary>
/// <remarks>
/// <para>
/// <b>电量、batteryState 与排队原因读分配板</b>（<see cref="ChargingAllocationBoard.LatestCompletedPass"/>，宿主单例；调度 10-02 批准的做法，
/// 同 cs#392 的空闲返回结论板）：这三样服务端不落库，只在派车轮把空闲车交给充电分配时从 RIoT 读、判一次。只取最近一轮已完成的分配：
/// 车不在其中（不空闲、在途、在干别的活）或服务启动以来还没完成过一轮时写没评估；那一轮走完超过 <see cref="PassLivenessPollIntervals"/> 个
/// 轮询间隔时，分配没在跑，每辆车都写没评估。电量那一格同样受窗口约束：窗口外不显示任何读数。用途、周期与码读的是库里的落库事实，照常给。
/// </para>
/// <para>
/// 听不到的车只进 <c>unavailableVehicles</c>（只有车号与原因），不给它失联前的电量、用途、周期或码（REQ-0269，与车辆用途卡片同一个判定）。
/// 它被持有的桩、它的人工充电等待与清桩是服务端的记录，照样出现在充电桩与暂停卡片上并标出失联。
/// </para>
/// </remarks>
internal sealed class ChargingVehiclesQueryEndpoint : IDashboardQueryEndpoint
{
    /// <summary>
    /// 分配在不在跑的时效窗口是几个引擎轮询间隔（<c>JourneyRuntime:PollInterval</c>）：与空闲返回结论那一格同一个窗口（cs#392，
    /// <see cref="IdleReturnsQueryEndpoint.PassLivenessPollIntervals"/>），判的是「分配还在不在跑」，不是重算结论。
    /// </summary>
    internal const int PassLivenessPollIntervals = 3;

    private readonly VehicleRoster _roster;
    private readonly ChargingAllocationBoard _board;
    private readonly TimeSpan _passLiveness;
    private readonly TimeProvider _clock;

    public ChargingVehiclesQueryEndpoint()
        : this(Options.Create(new JourneyRuntimeOptions()), new ChargingAllocationBoard(), TimeProvider.System)
    {
    }

    /// <summary>
    /// 挂在宿主上时用这一个：名册从宿主的同一份配置建，分配板是充电分配写的那一块单例。分配板是必填的：宿主漏注册时看板查询在启动时就构造失败，
    /// 而不是静默成「从没评估过」。
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public ChargingVehiclesQueryEndpoint(IOptions<JourneyRuntimeOptions> options, ChargingAllocationBoard board)
        : this(options, board, TimeProvider.System)
    {
    }

    internal ChargingVehiclesQueryEndpoint(IOptions<JourneyRuntimeOptions> options, ChargingAllocationBoard board, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        _roster = new VehicleRoster(options);
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _passLiveness = options.Value.PollInterval * PassLivenessPollIntervals;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "charging-vehicles";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        DateTimeOffset now = _clock.GetUtcNow();
        ChargingDashboardFacts facts = await ChargingDashboardFacts.ReadAsync(dbContext, _roster, now, cancellationToken);
        ChargingBoardPass? pass = _board.LatestCompletedPass;
        // 窗口外的一轮不当作这一轮：它说的是分配停下之前的事。
        // 完成时刻在此刻之后说明时钟回拨过，与过期的一轮同样不可用（独立审查 S3，写法同 VehicleDynamicFactsCriterion）。
        string? notRunning = pass is not null && (pass.CompletedAt > now || now - pass.CompletedAt > _passLiveness)
            ? ChargingDashboardDescriptions.PassNotRunning(_passLiveness)
            : null;
        return new
        {
            vehicles = facts.Contact.Vehicles
                .Where(facts.Contact.InContact)
                .Select(vehicle => Vehicle(facts, vehicle, notRunning is null ? pass : null, notRunning))
                .ToArray(),
            unavailableVehicles = facts.Contact.Unavailable(),
        };
    }

    private static object Vehicle(ChargingDashboardFacts facts, FleetVehicle vehicle, ChargingBoardPass? pass, string? notRunning)
    {
        VehiclePurposeClaimRow? claim = facts.Claims.GetValueOrDefault(vehicle.VehicleKey);
        ChargingCycleRow? cycle = facts.OpenCycles.SingleOrDefault(row => row.VehicleKey == vehicle.VehicleKey);
        JourneyRuntimeRow? journey = cycle is null ? null : facts.OpenChargingJourneys.GetValueOrDefault(cycle.JourneyId);
        string state = cycle?.WireState ?? ChargingCycleWireStates.NotCharging;
        ManualChargingHoldRow? manual = facts.ManualHolds.SingleOrDefault(row => row.VehicleKey == vehicle.VehicleKey);
        ChargingBoardVerdict? verdict = pass?.Verdicts.GetValueOrDefault(vehicle.AgvId);
        ChargingBoardObservation? observed = pass?.Observations.GetValueOrDefault(vehicle.AgvId);
        string? notEvaluated = notRunning
                               ?? (pass is null ? ChargingDashboardDescriptions.NoPassCompletedYet
                                   : verdict is null && observed is null ? ChargingDashboardDescriptions.NotEvaluatedThisPass
                                   : null);
        return new
        {
            agvId = vehicle.AgvId,
            vehicleKey = vehicle.VehicleKey,
            allocation = new
            {
                // 没评估时这里只有那一句话：电量、batteryState、结论一个值都不给，不让旧值出现在数据面里。
                note = notEvaluated,
                passCompletedAt = notEvaluated is null ? pass?.CompletedAt : null,
                batteryPercent = notEvaluated is null ? observed?.BatteryPercent : null,
                riotBatteryState = notEvaluated is null ? observed?.RiotBatteryState : null,
                batteryObservedAt = notEvaluated is null ? observed?.ObservedAt : null,
                batteryState = notEvaluated is null ? observed?.BatteryState : null,
                batteryStateDescription = notEvaluated is null && observed is not null
                    ? ChargingDashboardDescriptions.BatteryStateProjections.GetValueOrDefault(observed.BatteryState)
                    : null,
                reason = notEvaluated is null ? verdict?.Reason : null,
                reasonDescription = notEvaluated is null && verdict is not null
                    ? ChargingDashboardDescriptions.DescribeAllocationReason(verdict.Reason) ?? "服务端记下的原因没有中文说明，请报开发"
                    : null,
                detail = notEvaluated is null ? verdict?.Detail : null,
            },
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
