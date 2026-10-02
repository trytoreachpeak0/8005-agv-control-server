using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 车队视图「充电桩」的数据面（批次9-10，control-server#408；REQ-0171、REQ-0173、REQ-0177、REQ-0288，规格 5.5）：当前生效的名册版本与它的
/// 批准、变更记录；名册为空时的醒目提示与正在等人工充电的车；每个桩的阶段、持有车辆、进入该状态的时刻、占了多久、为什么还没放，以及
/// 它还没恢复的分配暂停。
/// </summary>
/// <remarks>
/// <para>
/// <b>桩独占视图</b>（cs#405 审查 S1，调度对齐）：充满之后充电旅程就收尾了，阻断卡片上看不到这台车，桩却仍归它。所以这里按桩列：
/// 谁占着、在哪个阶段（预占／占用／充满待离桩／清桩中／失败待放桩），自何时、占了多久、要等什么才放。阶段只读库里落下的周期与清桩行，
/// 不重算放桩的三项确认——那三项每轮从 RIoT 现读、只写日志，看板读不到「缺的是哪一项」，超过告警窗口时指到那条日志（事件 2247、2249）。
/// </para>
/// <para>
/// <b>列表是「当前名册里的桩」并上「此刻还被持有的桩」并上「还在暂停中的桩」</b>：名册换版或置空，旧版本下取得的预占与占用并不跟着放
/// （与等待点同一个合并，<see cref="StationHoldings.MergeWithHeld"/>）；暂停事件不随名册消失（REQ-0288 维修暂停保留名册身份）。
/// </para>
/// <para>
/// 持有车辆失联时独占行照给、标出失联，与等待点相同：那是服务端此刻仍为它保留这个桩的事实，不是车辆的读数。
/// </para>
/// </remarks>
internal sealed class ChargersQueryEndpoint : IDashboardQueryEndpoint
{
    internal const string NotInCurrentRosterStillHeld = "NOT_IN_CURRENT_ROSTER_STILL_HELD";
    internal const string NotInCurrentRosterOnHold = "NOT_IN_CURRENT_ROSTER_ON_HOLD";

    /// <summary>名册为空时整张卡片顶上那一句（规格 5.5「不静默」）。</summary>
    internal const string RosterEmptyBanner = "名册为空：自动充电已停，需要充电的车等人工充电";

    private readonly VehicleRoster _roster;
    private readonly JourneyRuntimeOptions _options;
    private readonly TimeProvider _clock;

    public ChargersQueryEndpoint()
        : this(Options.Create(new JourneyRuntimeOptions()), TimeProvider.System)
    {
    }

    /// <summary>挂在宿主上时用这一个：名册与告警窗口都从宿主的同一份运行时配置取，不要求宿主另外注册名册。</summary>
    [ActivatorUtilitiesConstructor]
    public ChargersQueryEndpoint(IOptions<JourneyRuntimeOptions> options)
        : this(options, TimeProvider.System)
    {
    }

    internal ChargersQueryEndpoint(IOptions<JourneyRuntimeOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        _roster = new VehicleRoster(options);
        _options = options.Value;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "chargers";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        DateTimeOffset now = _clock.GetUtcNow();
        ChargingDashboardFacts facts = await ChargingDashboardFacts.ReadAsync(dbContext, _roster, now, cancellationToken);

        List<object> chargers = [];
        foreach ((ChargerRosterEntryRow? entry, StationExclusivityRow? holding) in StationHoldings.MergeWithHeld(
                     facts.RosterEntries, entry => (entry.MapId, entry.StationId), facts.Exclusivities))
        {
            int mapId = entry?.MapId ?? holding!.MapId;
            int stationId = entry?.StationId ?? holding!.StationId;
            chargers.Add(Charger(facts, entry, holding, mapId, stationId, entry is null ? NotInCurrentRosterStillHeld : null, now));
        }
        foreach ((int mapId, int stationId) in facts.OpenStationHolds
                     .Select(hold => (hold.MapId, hold.StationId))
                     .Distinct()
                     .Where(key => !facts.RosterEntries.Any(entry => (entry.MapId, entry.StationId) == key) &&
                                   !facts.Exclusivities.Any(row => (row.MapId, row.StationId) == key))
                     .OrderBy(key => key.MapId).ThenBy(key => key.StationId))
        {
            chargers.Add(Charger(facts, entry: null, holding: null, mapId, stationId, NotInCurrentRosterOnHold, now));
        }

        return new
        {
            roster = Roster(facts),
            rosterEmpty = facts.RosterEmpty,
            rosterEmptyBanner = facts.RosterEmpty ? RosterEmptyBanner : null,
            // 「哪几台车在等人工充电」：服务端持有的人工充电等待（不论原因），名册为空时这就是退化的那几台。
            manualChargingHoldVehicles = facts.ManualHolds
                .Select(hold => new
                {
                    agvId = facts.AgvIdOf(hold.VehicleKey),
                    reason = hold.Reason,
                    reasonDescription = ChargingDashboardDescriptions.ManualHoldReasons.GetValueOrDefault(hold.Reason),
                    since = hold.Since,
                })
                .ToArray(),
            rosterHistory = facts.RosterHistory
                .Select(version => new
                {
                    version = version.Version,
                    loadedAt = version.LoadedAt,
                    approvedBy = version.ApprovedBy,
                    approvalBasis = version.ApprovalBasis,
                    changeNote = version.ChangeNote,
                    chargerCount = facts.StationNames.Keys.Count(key => key.Version == version.Version),
                })
                .ToArray(),
            chargers = chargers.ToArray(),
            hostEntries = ChargingDashboardDescriptions.HostEntries,
            unavailableVehicles = facts.Contact.Unavailable(),
        };
    }

    private static object Roster(ChargingDashboardFacts facts) => facts.Roster is not { } current
        ? new
        {
            state = "NEVER_IMPORTED",
            stateDescription = ChargingDashboardDescriptions.OwnAlarmCodes[ChargingDashboardDescriptions.AlarmRosterNeverImported],
            version = (long?)null,
            loadedAt = (DateTimeOffset?)null,
            approvedBy = (string?)null,
            approvalBasis = (string?)null,
            changeNote = (string?)null,
            contentSha256 = (string?)null,
            snapshotId = (string?)null,
            source = (string?)null,
            chargerCount = 0,
        }
        : new
        {
            state = facts.RosterEntries.Length == 0 ? "EMPTY" : "ACTIVE",
            stateDescription = facts.RosterEntries.Length == 0
                ? ChargingDashboardDescriptions.OwnAlarmCodes[ChargingDashboardDescriptions.AlarmRosterEmpty]
                : "名册生效中：下面列出的桩接受自动充电分配（暂停中的除外）",
            version = (long?)current.Version,
            loadedAt = (DateTimeOffset?)current.LoadedAt,
            approvedBy = (string?)current.ApprovedBy,
            approvalBasis = (string?)current.ApprovalBasis,
            changeNote = current.ChangeNote,
            contentSha256 = (string?)current.ContentSha256,
            snapshotId = current.SnapshotId,
            source = (string?)current.Source,
            chargerCount = facts.RosterEntries.Length,
        };

    private object Charger(
        ChargingDashboardFacts facts,
        ChargerRosterEntryRow? entry,
        StationExclusivityRow? holding,
        int mapId,
        int stationId,
        string? rosterNote,
        DateTimeOffset now)
    {
        ChargingCycleRow? cycle = holding is null ? null : facts.CyclesOfHeldChargers.GetValueOrDefault(holding.JourneyId);
        StationClearanceRow? clearance = cycle is null ? null : facts.OpenClearances.GetValueOrDefault(cycle.CycleId);
        JourneyRuntimeRow? journey = holding is null ? null : facts.OpenChargingJourneys.GetValueOrDefault(holding.JourneyId);
        string? purpose = holding is null ? null
            : facts.Claims.GetValueOrDefault(holding.VehicleKey) is { } claim && claim.JourneyId == holding.JourneyId ? claim.Purpose
            : null;
        (string stage, DateTimeOffset? stageSince) = ChargingDashboardFacts.Stage(holding, cycle);
        TimeSpan? overdueBy = ChargingDashboardFacts.OverdueBy(stage, stageSince, now, _options.OwnOrderRebuildRepeatWindow);
        ChargingStationAllocationHoldRow[] holds =
            [.. facts.OpenStationHolds.Where(hold => hold.MapId == mapId && hold.StationId == stationId)];
        return new
        {
            mapId,
            stationId,
            stationName = entry?.StationName ?? (cycle is null ? null : facts.StationNameOf(cycle.ChargerRosterVersion, mapId, stationId)),
            entryStationId = entry?.EntryStationId,
            exitStationId = entry?.ExitStationId,
            // 名册里这个桩的候选车：为空即对投运名册里的全部车开放。
            vehicleScope = entry is null
                ? []
                : facts.RosterScopes.Where(scope => scope.MapId == mapId && scope.StationId == stationId)
                    .Select(scope => facts.AgvIdOf(scope.VehicleKey)).Order(StringComparer.Ordinal).ToArray(),
            inCurrentRoster = entry is not null,
            rosterNote,
            rosterNoteDescription = rosterNote switch
            {
                NotInCurrentRosterStillHeld =>
                    "这个桩已不在当前名册里（名册换版或置空），但仍被下面这辆车持有：名册变了不放桩，要等它的放桩条件满足",
                NotInCurrentRosterOnHold => "这个桩已不在当前名册里，但它的分配暂停还没恢复：暂停事件不随名册消失，恢复要做恢复确认",
                _ => null,
            },
            holding = StationHoldings.Project(holding, facts.Contact, StationExclusivityKinds.Charger, purpose),
            stage,
            stageDescription = stage == ChargingDashboardDescriptions.StageClearing
                ? ChargingDashboardDescriptions.WhileClearing(journey?.BlockReasonCode, ChargingDashboardDescriptions.ChargerStages[stage])
                : ChargingDashboardDescriptions.ChargerStages[stage],
            stageSince,
            stageSeconds = stageSince is { } start ? (long?)Math.Max(0, (now - start).TotalSeconds) : null,
            overdue = overdueBy is not null,
            overdueDescription = overdueBy is null
                ? null
                : $"已超过 {ChargingDashboardDescriptions.Minutes(_options.OwnOrderRebuildRepeatWindow)} 分钟告警窗口"
                  + $"（JourneyRuntime:OwnOrderRebuildRepeatWindow）仍没放：三项确认缺哪一项见服务端日志事件 "
                  + ChargingDashboardFacts.OverdueEventId(stage),
            cycle = cycle is null
                ? null
                : new
                {
                    cycleId = cycle.CycleId,
                    phase = cycle.Phase,
                    phaseDescription = ChargingDashboardDescriptions.CyclePhases.GetValueOrDefault(cycle.Phase),
                    chargingCycleState = cycle.WireState,
                    chargingCycleStateDescription = ChargingDashboardDescriptions.CycleStates.GetValueOrDefault(cycle.WireState),
                    completedAt = cycle.CompletedAt,
                    endedAt = cycle.EndedAt,
                    endReason = cycle.EndReason,
                    endReasonDescription = ChargingDashboardDescriptions.DescribeChargingCode(cycle.EndReason),
                    chargerRosterVersion = cycle.ChargerRosterVersion,
                    chargingPolicyVersion = cycle.ChargingPolicyVersion,
                },
            clearance = clearance is null ? null : facts.Clearance(clearance),
            journeyCode = journey?.BlockReasonCode,
            journeyCodeDescription = ChargingDashboardDescriptions.DescribeChargingCode(journey?.BlockReasonCode),
            journeyCodeSince = journey?.BlockReasonCode is null ? null : journey.BlockReasonSince,
            allocationHeld = holds.Length > 0,
            allocationHolds = holds.Select(facts.StationHold).ToArray(),
        };
    }
}
