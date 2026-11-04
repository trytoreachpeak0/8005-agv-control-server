using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 车队视图「充电告警」的数据面（批次9-10，control-server#408；规格 5.5、8.6，REQ-0287）：此刻还成立的充电告警，按严重程度排，每条写明码、
/// 中文说明与「现场该做什么」。本批不做告警推送，现场看告警只靠看板与日志，所以这里列的都是库里此刻还成立的状态，不是日志的回放。
/// </summary>
/// <remarks>
/// <para>告警的来源，全部是库里落下的事实：</para>
/// <list type="bullet">
/// <item>名册为空或从没导入过（<c>ChargerRosterVersions</c>），连同正在等人工充电的车；</item>
/// <item>每一条服务端持有的人工充电等待（<c>ManualChargingHolds</c>）；</item>
/// <item>每一次还没恢复的桩分配暂停（充不上、中断、无进展、维修）；</item>
/// <item>未完成的充电旅程上此刻写着的码（到桩不充电、RIoT 车辆观测丢失、电量遥测丢失、清桩中旧单被恢复等）；</item>
/// <item>充满待离桩、失败待放桩超过告警窗口的桩（与引擎事件 2249、2247 同一个窗口）；</item>
/// <item>车载端会话失联、而服务端还为它记着充电周期、桩、清桩或人工充电等待的车（REQ-0287 的第三种失联）；</item>
/// <item>唯一不在库里的一种：低于强制充电线、又过不了出发前安全门而分不到桩的车，读最近一轮已完成的充电分配（分配板，宿主单例），
/// 细节点名缺哪一项（control-server#865）。</item>
/// </list>
/// <para>
/// <b>无合格桩</b>不单列：它是充电分配的结论，看逐车卡片的排队原因（<c>notReadable</c> 里写明）。<b>中断、无进展</b>（批次9-09）以桩的暂停与旅程码出现（<c>notReadable</c> 里写明）。
/// </para>
/// </remarks>
internal sealed class ChargingAlarmsQueryEndpoint : IDashboardQueryEndpoint
{
    private readonly VehicleRoster _roster;
    private readonly JourneyRuntimeOptions _options;
    private readonly ChargingAllocationBoard _board;
    private readonly TimeSpan _passLiveness;
    private readonly TimeProvider _clock;

    public ChargingAlarmsQueryEndpoint()
        : this(Options.Create(new JourneyRuntimeOptions()), new ChargingAllocationBoard(), TimeProvider.System)
    {
    }

    /// <summary>
    /// 挂在宿主上时用这一个：名册与告警窗口都从宿主的同一份运行时配置取，不要求宿主另外注册名册；分配板是充电分配写的那一块单例
    /// （control-server#865：低于强制线又过不了出发门的车只在那里，不在库里）。
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public ChargingAlarmsQueryEndpoint(IOptions<JourneyRuntimeOptions> options, ChargingAllocationBoard board)
        : this(options, board, TimeProvider.System)
    {
    }

    internal ChargingAlarmsQueryEndpoint(IOptions<JourneyRuntimeOptions> options, ChargingAllocationBoard board, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        _roster = new VehicleRoster(options);
        _options = options.Value;
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _passLiveness = options.Value.PollInterval * ChargingVehiclesQueryEndpoint.PassLivenessPollIntervals;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "charging-alarms";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        DateTimeOffset now = _clock.GetUtcNow();
        ChargingDashboardFacts facts = await ChargingDashboardFacts.ReadAsync(dbContext, _roster, now, cancellationToken);
        List<Alarm> alarms = [];

        if (facts.RosterEmpty)
        {
            string[] waiting = [.. facts.ManualHolds.Select(hold => facts.AgvIdOf(hold.VehicleKey))];
            alarms.Add(new Alarm(
                facts.Roster is null ? ChargingDashboardDescriptions.AlarmRosterNeverImported : ChargingDashboardDescriptions.AlarmRosterEmpty,
                AgvId: null,
                MapId: null,
                StationId: null,
                Since: facts.Roster?.LoadedAt,
                Detail: waiting.Length == 0 ? "此刻没有车在等人工充电" : $"正在等人工充电的车：{string.Join("、", waiting)}"));
        }

        foreach (ManualChargingHoldRow hold in facts.ManualHolds)
        {
            alarms.Add(new Alarm(
                ChargingDashboardDescriptions.AlarmManualChargingHold,
                facts.AgvIdOf(hold.VehicleKey),
                MapId: null,
                StationId: null,
                hold.Since,
                $"{hold.Reason}：{ChargingDashboardDescriptions.ManualHoldReasons.GetValueOrDefault(hold.Reason) ?? "服务端记下的原因没有中文说明，请报开发"}"));
        }

        foreach (ChargingStationAllocationHoldRow hold in facts.OpenStationHolds)
        {
            alarms.Add(new Alarm(
                hold.Trigger,
                hold.VehicleKey is null ? null : facts.AgvIdOf(hold.VehicleKey),
                hold.MapId,
                hold.StationId,
                hold.HeldAt,
                $"桩分配暂停 {hold.HoldId}，根因 {hold.RootCause}"));
        }

        foreach (JourneyRuntimeRow journey in facts.OpenChargingJourneys.Values
                     .Where(row => row.BlockReasonCode is not null))
        {
            ChargingCycleRow? cycle = facts.OpenCycles.SingleOrDefault(row => row.JourneyId == journey.JourneyId);
            alarms.Add(new Alarm(
                journey.BlockReasonCode!,
                facts.AgvIdOf(journey.VehicleKey),
                cycle?.MapId,
                cycle?.StationId,
                journey.BlockReasonSince,
                $"充电旅程 {journey.JourneyId}"));
        }

        foreach (StationExclusivityRow holding in facts.Exclusivities)
        {
            ChargingCycleRow? cycle = facts.CyclesOfHeldChargers.GetValueOrDefault(holding.JourneyId);
            (string stage, DateTimeOffset? since) = ChargingDashboardFacts.Stage(holding, cycle);
            if (ChargingDashboardFacts.OverdueBy(stage, since, now, _options.OwnOrderRebuildRepeatWindow) is { } overdue)
            {
                alarms.Add(new Alarm(
                    stage,
                    facts.AgvIdOf(holding.VehicleKey),
                    holding.MapId,
                    holding.StationId,
                    since,
                    $"已 {ChargingDashboardDescriptions.Minutes(overdue)} 分钟没放（告警窗口 "
                    + $"{ChargingDashboardDescriptions.Minutes(_options.OwnOrderRebuildRepeatWindow)} 分钟），缺哪一项见服务端日志事件 "
                    + ChargingDashboardFacts.OverdueEventId(stage)));
            }
        }

        // control-server#865: below the mandatory line (every vehicle the allocation weighs is) and held back by the departure
        // gate. The gate stays shut; this says which slot or fact to go and look at while the battery falls. Read from the
        // latest completed allocation pass, and only while the allocation is running, as the per-vehicle card reads it.
        ChargingBoardPass? pass = _board.LatestCompletedPass;
        if (pass is not null && pass.CompletedAt <= now && now - pass.CompletedAt <= _passLiveness)
        {
            foreach ((string agvId, ChargingBoardVerdict verdict) in pass.Verdicts
                         .Where(pair => pair.Value.Reason == ChargingAllocationReasons.DepartureNotProven)
                         .OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                int? battery = pass.Observations.GetValueOrDefault(agvId)?.BatteryPercent;
                alarms.Add(new Alarm(
                    ChargingDashboardDescriptions.AlarmBelowMandatoryDepartureNotProven,
                    agvId,
                    MapId: null,
                    StationId: null,
                    Since: null,
                    ChargingDashboardDescriptions.DescribeDepartureGaps(battery, ChargingDepartureGaps.Of(verdict.Detail))));
            }
        }

        foreach (FleetVehicle vehicle in facts.Contact.Vehicles.Where(vehicle => !facts.Contact.InContact(vehicle)))
        {
            List<string> held = [];
            if (facts.OpenCycles.Any(cycle => cycle.VehicleKey == vehicle.VehicleKey))
            {
                held.Add("充电周期");
            }
            if (facts.Exclusivities.Any(row => row.VehicleKey == vehicle.VehicleKey))
            {
                held.Add("充电桩");
            }
            if (facts.ManualHolds.Any(row => row.VehicleKey == vehicle.VehicleKey))
            {
                held.Add("人工充电等待");
            }
            if (held.Count > 0)
            {
                alarms.Add(new Alarm(
                    JourneyRuntimeEngine.OnboardSessionLostReason,
                    vehicle.AgvId,
                    MapId: null,
                    StationId: null,
                    Since: null,
                    $"服务端仍为它记着：{string.Join("、", held)}"));
            }
        }

        // Ordered in memory: SQLite cannot ORDER BY a DateTimeOffset. Most severe first, then longest standing, unknown starts first.
        return new
        {
            alarms = alarms
                .OrderBy(alarm => ChargingDashboardDescriptions.SeverityRank(ChargingDashboardDescriptions.SeverityOf(alarm.Code)))
                .ThenBy(alarm => alarm.Since ?? DateTimeOffset.MinValue)
                .ThenBy(alarm => alarm.AgvId, StringComparer.Ordinal)
                .Select(alarm => Fact(alarm, now))
                .ToArray(),
            notReadable = new[]
            {
                ChargingDashboardDescriptions.NoChargerAvailableNote,
                ChargingDashboardDescriptions.InterruptionAlarmsNote,
            },
            unavailableVehicles = facts.Contact.Unavailable(),
        };
    }

    private static object Fact(Alarm alarm, DateTimeOffset now)
    {
        string severity = ChargingDashboardDescriptions.SeverityOf(alarm.Code);
        return new
        {
            severity,
            severityDescription = ChargingDashboardDescriptions.Severities[severity],
            code = alarm.Code,
            codeDescription = ChargingDashboardDescriptions.DescribeAlarm(alarm.Code) ?? "服务端记下的码没有中文说明，请报开发",
            fieldAction = ChargingDashboardDescriptions.FieldActions.GetValueOrDefault(alarm.Code) ?? "按上面的说明处理，拿不准时报值班工程师",
            agvId = alarm.AgvId,
            mapId = alarm.MapId,
            stationId = alarm.StationId,
            since = alarm.Since,
            standingSeconds = alarm.Since is { } since ? (long?)Math.Max(0, (now - since).TotalSeconds) : null,
            detail = alarm.Detail,
        };
    }

    private sealed record Alarm(string Code, string? AgvId, int? MapId, int? StationId, DateTimeOffset? Since, string Detail);
}
