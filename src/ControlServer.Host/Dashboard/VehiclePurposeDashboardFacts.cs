using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 用途与站点独占的看板数据面共用的读法（批次8-21，control-server#392）。
/// </summary>
/// <remarks>
/// <para>
/// <b>给后面的卡片复用。</b>车辆用途、等待点、公共站点与空闲返回四张卡片都从这里读；批次9 的充电桩卡片（control-server#408）
/// 读 <see cref="StationExclusivityKinds.Charger"/> 时用同一个 <see cref="StationHoldings.ReadAsync"/> 与 <see cref="StationHoldings.Project"/>，
/// 车辆那一侧用 <see cref="DashboardFleetContact"/> 与 <see cref="VehiclePurposeFacts"/>，不另写一份。
/// </para>
/// <para>
/// <b>只读库里落下的事实，不在这里另算。</b>用途占有（<c>VehiclePurposeClaims</c>）与站点独占（<c>StationExclusivities</c>）各是一车一行、一站一行，
/// 由主键仲裁；这里只投影它们，不判断它们该不该在。
/// </para>
/// </remarks>
internal static class DashboardDescriptions
{
    /// <summary>车辆用途的中文说明。键是 <see cref="VehiclePurposes.All"/> 的每个取值；新加一个用途而忘了说明，看板测试就红。</summary>
    internal static IReadOnlyDictionary<string, string> Purposes { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VehiclePurposes.Transport] = "搬运：这辆车正替一趟搬运旅程干活，不接别的用途",
            [VehiclePurposes.Charging] = "充电：这辆车正为一次充电开往充电桩或在桩上充电，不接别的用途",
            [VehiclePurposes.ClearingMaintenance] = "清桩或维护：这辆车被占作清桩或维护，不接别的用途",
            [VehiclePurposes.IdleReturn] = "空闲返回：这辆车没有搬运任务，正开回一个等待点，到点收敛之前不接别的用途",
        };

    /// <summary>站点独占两种状态的中文说明（规格 5.4「一行两状态」），等待点与固定公共站点的口径。</summary>
    internal static IReadOnlyDictionary<string, string> StationStates { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StationExclusivityStates.Reserved] = "预占：已为这辆车留下，车还在路上，别的车不会被派来",
            [StationExclusivityStates.Occupied] = "占用：车已到点，这个点一直归它，直到离点证据满足（车确实离开）才放",
        };

    /// <summary>
    /// 充电桩的两种状态（批次9-10，control-server#408）。放桩的条件与等待点不同：不是「离点证据」，而是 <c>REQ-0173</c> 的三项确认
    /// （充电已停、车不在桩上、桩位可确认空闲）；清桩中的桩还要人工清桩确认并且旧单终结（control-server#406）。具体是哪一种，
    /// 充电桩卡片按周期另写一格。
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ChargerStates { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StationExclusivityStates.Reserved] =
                "预占：已为这辆车留下，车还在去桩的路上（或这一次充电已失败、桩还没放），别的车不会被派来",
            [StationExclusivityStates.Occupied] =
                "占用：车已到桩，这个桩一直归它；充满后也不放，要等充电已停、车离开桩、桩位可确认空闲三项都确认了才放"
                + "（清桩中的桩另要人工清桩确认并且旧单终结）",
        };

    /// <summary>
    /// 一种站点、一个状态的中文说明，按站点种类取（cs#392 审查对齐第 1 条）：「直到离点证据满足才放」对充电桩不成立。没有说明时答空。
    /// </summary>
    internal static string? StationState(string stationKind, string state) =>
        (stationKind == StationExclusivityKinds.Charger ? ChargerStates : StationStates).GetValueOrDefault(state);

    /// <summary>站点独占的种类。</summary>
    internal static IReadOnlyDictionary<string, string> StationKinds { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StationExclusivityKinds.WaitingPoint] = "等待点",
            [StationExclusivityKinds.FixedTaskStation] = "固定公共站点",
            [StationExclusivityKinds.Charger] = "充电桩",
        };
}

/// <summary>
/// 名册上的车此刻听不听得到。听不到的车，车辆一侧的卡片只把它列进 <c>unavailableVehicles</c>，不给它失联前的用途、步骤或结论（REQ-0269）。
/// </summary>
/// <remarks>
/// 在线判定与车队会话卡片同一个：<see cref="SessionLiveness.HeardFromAsync(ControlServerDbContext, DateTimeOffset, CancellationToken)"/>，
/// 六秒内听到过当前这一代会话。看板只读库、从不调 RIoT，所以「RIoT 读不到」在这里没有单独的信号；会话听不到时一律按失联写。
/// </remarks>
internal sealed class DashboardFleetContact
{
    private readonly HashSet<string> _heard;

    private DashboardFleetContact(IReadOnlyList<FleetVehicle> vehicles, HashSet<string> heard)
    {
        Vehicles = vehicles;
        _heard = heard;
    }

    public IReadOnlyList<FleetVehicle> Vehicles { get; }

    public static async Task<DashboardFleetContact> ReadAsync(
        ControlServerDbContext dbContext,
        VehicleRoster roster,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(roster);
        HashSet<string> heard = await SessionLiveness.HeardFromAsync(dbContext, now, cancellationToken);
        return new DashboardFleetContact(roster.Vehicles, heard);
    }

    public bool InContact(FleetVehicle vehicle) => _heard.Contains(vehicle.AgvId);

    public FleetVehicle? ByVehicleKey(string vehicleKey) =>
        Vehicles.SingleOrDefault(vehicle => string.Equals(vehicle.VehicleKey, vehicleKey, StringComparison.Ordinal));

    /// <summary>失联的车：只有车号与原因，没有别的值。</summary>
    public object[] Unavailable() =>
    [
        .. Vehicles.Where(vehicle => !InContact(vehicle))
            .Select(vehicle => new { agvId = vehicle.AgvId, reason = VehicleAlarmProjection.LinkDownReason }),
    ];
}

/// <summary>每辆车此刻的用途占有（一车至多一行）。</summary>
internal static class VehiclePurposeFacts
{
    public static async Task<Dictionary<string, VehiclePurposeClaimRow>> ReadAsync(
        ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        VehiclePurposeClaimRow[] claims = await dbContext.Set<VehiclePurposeClaimRow>().AsNoTracking()
            .ToArrayAsync(cancellationToken);
        return claims.ToDictionary(claim => claim.VehicleKey, StringComparer.Ordinal);
    }

    /// <summary>
    /// 持有者是哪一种：空闲返回记录、充电旅程、清桩中的充电旅程，或搬运旅程（从持有者的旅程 id 前缀读，与引擎认它们的写法相同）。
    /// </summary>
    /// <param name="purpose">
    /// 这趟旅程此刻占着车的用途（知道时给）。已确认充不上之后用途转为 <c>CLEARING_MAINTENANCE</c>，持有者仍是原来那趟充电旅程
    /// （control-server#406，<c>JourneyRuntimeEngine.UnableToCharge.cs</c> 转用途时不换旅程 id）；只看前缀会把它读成普通的充电旅程
    /// （cs#392 审查对齐第 3 条），所以用途是 <c>CLEARING_MAINTENANCE</c> 时答 <c>CLEARING_MAINTENANCE</c>。
    /// </param>
    public static string HolderKind(string journeyId, string? purpose = null) =>
        purpose == VehiclePurposes.ClearingMaintenance ? "CLEARING_MAINTENANCE"
        : journeyId.StartsWith(IdleReturnIdentity.JourneyIdPrefix, StringComparison.Ordinal) ? "IDLE_RETURN_RECORD"
        : journeyId.StartsWith(ChargingIdentity.JourneyIdPrefix, StringComparison.Ordinal) ? "CHARGING_JOURNEY"
        : "JOURNEY";

    public static string HolderKindDescription(string journeyId, string? purpose = null) => HolderKind(journeyId, purpose) switch
    {
        "IDLE_RETURN_RECORD" => "空闲返回记录",
        "CHARGING_JOURNEY" => "充电旅程",
        "CLEARING_MAINTENANCE" => "清桩中的充电旅程",
        _ => "旅程",
    };
}

/// <summary>
/// 站点独占（等待点、固定公共站点、充电桩共用的一站一行原语）的读法与统一投影：状态、持有车辆、持有旅程、进入该状态的时刻、所依据的配置版本。
/// </summary>
internal static class StationHoldings
{
    public static async Task<StationExclusivityRow[]> ReadAsync(
        ControlServerDbContext dbContext, string stationKind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(stationKind);
        StationExclusivityRow[] rows = await dbContext.Set<StationExclusivityRow>().AsNoTracking()
            .Where(row => row.StationKind == stationKind)
            .ToArrayAsync(cancellationToken);
        return [.. rows.OrderBy(row => row.MapId).ThenBy(row => row.StationId)];
    }

    /// <summary>
    /// 一个站点此刻被谁以哪种状态持有；没被持有时 <paramref name="held"/> 为空，状态是 <c>FREE</c>。
    /// </summary>
    /// <remarks>
    /// 持有车辆失联时，独占行照样给：它是服务端此刻仍为那辆车保留着这个点的事实（REQ-0297 要求被引用的点照样列出），不是车辆的读数。
    /// 只是标出 <c>holderInContact = false</c>，卡片据此写「车辆失联」，不替一辆听不到的车说它此刻在不在点上。
    /// </remarks>
    /// <param name="stationKind">这个站点的种类（<see cref="StationExclusivityKinds"/>）：状态说明按种类取，结果里也带上它。</param>
    /// <param name="holderPurpose">持有旅程此刻占着车的用途（知道时给），见 <see cref="VehiclePurposeFacts.HolderKind"/>。</param>
    public static object Project(
        StationExclusivityRow? held, DashboardFleetContact contact, string stationKind, string? holderPurpose = null)
    {
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentException.ThrowIfNullOrWhiteSpace(stationKind);
        if (held is null)
        {
            return new
            {
                stationKind,
                stationKindDescription = DashboardDescriptions.StationKinds.GetValueOrDefault(stationKind),
                status = "FREE",
                statusDescription = "空闲：没有车预占或占用",
                holderAgvId = (string?)null,
                holderVehicleKey = (string?)null,
                holderJourneyId = (string?)null,
                holderKind = (string?)null,
                holderInContact = (bool?)null,
                stateSince = (DateTimeOffset?)null,
                waitingPointVersion = (long?)null,
                chargerRosterVersion = (long?)null,
            };
        }

        FleetVehicle? vehicle = contact.ByVehicleKey(held.VehicleKey);
        return new
        {
            stationKind = held.StationKind,
            stationKindDescription = DashboardDescriptions.StationKinds.GetValueOrDefault(held.StationKind),
            status = held.State,
            statusDescription = DashboardDescriptions.StationState(held.StationKind, held.State)
                                ?? "服务端记下的状态没有中文说明，请报开发",
            holderAgvId = vehicle?.AgvId,
            holderVehicleKey = (string?)held.VehicleKey,
            holderJourneyId = (string?)held.JourneyId,
            holderKind = (string?)VehiclePurposeFacts.HolderKindDescription(held.JourneyId, holderPurpose),
            holderInContact = (bool?)(vehicle is not null && contact.InContact(vehicle)),
            stateSince = (DateTimeOffset?)held.StateSince,
            waitingPointVersion = held.WaitingPointVersion,
            chargerRosterVersion = held.ChargerRosterVersion,
        };
    }

    /// <summary>
    /// 「当前登记（或生效）的站点」并上「此刻还被持有的独占」：等待点、公共站点、充电桩三个数据面共用这一份（cs#392 审查对齐第 2 条）。
    /// </summary>
    /// <remarks>
    /// 登记换了版本，旧版本下取得的预占与占用并不跟着消失（REQ-0293、REQ-0297）。所以结果先是登记里的每个站点（按给的顺序，配上它此刻的独占，
    /// 没有为空），后面跟上不在登记里却仍被持有的独占（<c>Entry</c> 为空）；后者由调用方标明「不在当前登记里、仍被持有」。
    /// </remarks>
    public static IReadOnlyList<(TEntry? Entry, StationExclusivityRow? Holding)> MergeWithHeld<TEntry>(
        IEnumerable<TEntry> registered,
        Func<TEntry, (int MapId, int StationId)> keyOf,
        IReadOnlyList<StationExclusivityRow> held)
        where TEntry : class
    {
        ArgumentNullException.ThrowIfNull(registered);
        ArgumentNullException.ThrowIfNull(keyOf);
        ArgumentNullException.ThrowIfNull(held);
        List<(TEntry?, StationExclusivityRow?)> merged = [];
        HashSet<(int MapId, int StationId)> listed = [];
        foreach (TEntry entry in registered)
        {
            (int mapId, int stationId) = keyOf(entry);
            listed.Add((mapId, stationId));
            merged.Add((entry, held.SingleOrDefault(row => row.MapId == mapId && row.StationId == stationId)));
        }
        foreach (StationExclusivityRow orphan in held.Where(row => !listed.Contains((row.MapId, row.StationId))))
        {
            merged.Add((null, orphan));
        }
        return merged;
    }
}
