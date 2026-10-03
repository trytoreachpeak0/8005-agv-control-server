using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 车队视图「公共站点占用」的数据面（批次8-21，control-server#392；REQ-0204 修订、批次8-20 control-server#391）：每张图生效绑定集里
/// 的每个固定公共站点，它的预占与占用——与等待点同一种显示（共用 <see cref="StationHoldings"/> 的投影）。
/// </summary>
/// <remarks>
/// 站点集合取每张图生效的任务类型绑定集（与「任务类型绑定与暂停」卡片同一个读法：<c>TaskTypeStationActiveBindingSets</c> 指向的版本）。
/// 此刻持有着、却已不在生效绑定集里的公共站点独占照样列出并标明（REQ-0297），它要等离点证据才放。
/// </remarks>
internal sealed class FixedTaskStationsQueryEndpoint : IDashboardQueryEndpoint
{
    internal const string NotInActiveBindingStillHeld = "NOT_IN_ACTIVE_BINDING_STILL_HELD";

    private readonly VehicleRoster _roster;
    private readonly TimeProvider _clock;

    public FixedTaskStationsQueryEndpoint()
        : this(new VehicleRoster(Options.Create(new JourneyRuntimeOptions())), TimeProvider.System)
    {
    }

    /// <summary>
    /// 挂在宿主上时用这一个：名册从宿主的同一份配置建（<see cref="VehicleRoster"/> 只读配置、建好不变，与引擎那份单例逐项相同），
    /// 不要求宿主另外注册名册——看板的最小宿主只配了运行时选项。
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public FixedTaskStationsQueryEndpoint(IOptions<JourneyRuntimeOptions> options)
        : this(new VehicleRoster(options), TimeProvider.System)
    {
    }

    internal FixedTaskStationsQueryEndpoint(VehicleRoster roster, TimeProvider clock)
    {
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "fixed-task-stations";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        DashboardFleetContact contact =
            await DashboardFleetContact.ReadAsync(dbContext, _roster, _clock.GetUtcNow(), cancellationToken);
        TaskTypeStationActiveBindingSetRow[] pointers = await dbContext.Set<TaskTypeStationActiveBindingSetRow>().AsNoTracking()
            .Where(row => row.ActiveVersion != null)
            .ToArrayAsync(cancellationToken);
        List<TaskTypeStationBindingRow> bindings = [];
        foreach (TaskTypeStationActiveBindingSetRow pointer in pointers)
        {
            bindings.AddRange(await dbContext.Set<TaskTypeStationBindingRow>().AsNoTracking()
                .Where(row => row.MapId == pointer.MapId && row.Version == pointer.ActiveVersion)
                .ToArrayAsync(cancellationToken));
        }
        StationExclusivityRow[] held =
            await StationHoldings.ReadAsync(dbContext, StationExclusivityKinds.FixedTaskStation, cancellationToken);

        List<object> stations = [];
        foreach ((IGrouping<(int MapId, int StationId), TaskTypeStationBindingRow>? station, StationExclusivityRow? holding) in
                 StationHoldings.MergeWithHeld(
                     bindings
                         .GroupBy(row => (row.MapId, StationId: row.StationRiotId))
                         .OrderBy(group => group.Key.MapId)
                         .ThenBy(group => group.Key.StationId),
                     group => group.Key,
                     held))
        {
            stations.Add(station is not null
                ? Station(
                    station.Key.MapId,
                    station.Key.StationId,
                    station.Select(row => row.StationName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).FirstOrDefault(),
                    [.. station.Select(row => row.TaskType).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                    station.First().Version,
                    holding,
                    note: null,
                    contact)
                : Station(
                    holding!.MapId, holding.StationId, stationName: null, taskTypes: [], bindingSetVersion: null, holding,
                    NotInActiveBindingStillHeld, contact));
        }

        return new
        {
            stations = stations.ToArray(),
            unavailableVehicles = contact.Unavailable(),
        };
    }

    private static object Station(
        int mapId,
        int stationId,
        string? stationName,
        string[] taskTypes,
        long? bindingSetVersion,
        StationExclusivityRow? holding,
        string? note,
        DashboardFleetContact contact) => new
        {
            mapId,
            stationId,
            stationName,
            taskTypes,
            bindingSetVersion,
            registrationNote = note,
            registrationNoteDescription = note == NotInActiveBindingStillHeld
                ? "这个站点已不在本图生效的任务类型绑定集里，但仍被下面这辆车预占或占用：要等离点证据满足才放"
                : null,
            holding = StationHoldings.Project(holding, contact, StationExclusivityKinds.FixedTaskStation),
        };
}
