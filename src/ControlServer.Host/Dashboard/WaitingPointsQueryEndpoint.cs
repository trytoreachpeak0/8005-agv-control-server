using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 车队视图「等待点」的数据面（批次8-21，control-server#392；REQ-0289、REQ-0293、REQ-0297）：当前生效的登记版本，每个等待点的状态
/// （空闲、预占、占用、停用）、持有车辆、进入该状态的时刻、所依据的登记版本与白名单。
/// </summary>
/// <remarks>
/// <para>
/// <b>停用或删除之后仍被引用的点照样列出。</b>登记换了版本，旧版本下取得的预占与占用并不跟着消失——它们要等离点证据才放
/// （REQ-0293）。所以列表是「当前登记里的点」并上「此刻持有着的等待点独占」：后者不在当前登记里、或在登记里却已停用的，标出
/// <c>registrationNote</c>（<c>DISABLED_STILL_HELD</c>、<c>NOT_IN_CURRENT_REGISTRATION_STILL_HELD</c>），不让它从看板上悄悄消失。
/// </para>
/// <para>
/// 状态读站点独占行（一站一行两状态），共用 <see cref="StationHoldings"/> 的投影；持有车辆失联时独占行照给、标出失联，见那里的说明。
/// </para>
/// </remarks>
internal sealed class WaitingPointsQueryEndpoint : IDashboardQueryEndpoint
{
    internal const string DisabledStillHeld = "DISABLED_STILL_HELD";
    internal const string NotInCurrentRegistrationStillHeld = "NOT_IN_CURRENT_REGISTRATION_STILL_HELD";

    private readonly VehicleRoster _roster;
    private readonly TimeProvider _clock;

    public WaitingPointsQueryEndpoint()
        : this(new VehicleRoster(Options.Create(new JourneyRuntimeOptions())), TimeProvider.System)
    {
    }

    /// <summary>
    /// 挂在宿主上时用这一个：名册从宿主的同一份配置建（<see cref="VehicleRoster"/> 只读配置、建好不变，与引擎那份单例逐项相同），
    /// 不要求宿主另外注册名册——看板的最小宿主只配了运行时选项。
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public WaitingPointsQueryEndpoint(IOptions<JourneyRuntimeOptions> options)
        : this(new VehicleRoster(options), TimeProvider.System)
    {
    }

    internal WaitingPointsQueryEndpoint(VehicleRoster roster, TimeProvider clock)
    {
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "waiting-points";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        DashboardFleetContact contact =
            await DashboardFleetContact.ReadAsync(dbContext, _roster, _clock.GetUtcNow(), cancellationToken);
        WaitingPointRegistrationVersion? registration =
            await WaitingPointRegistry.ReadCurrentFromAsync(dbContext, cancellationToken);
        StationExclusivityRow[] held =
            await StationHoldings.ReadAsync(dbContext, StationExclusivityKinds.WaitingPoint, cancellationToken);

        List<object> points = [];
        foreach (WaitingPointEntry point in registration?.Points ?? [])
        {
            StationExclusivityRow? holding = held.SingleOrDefault(row => row.MapId == point.MapId && row.StationId == point.StationId);
            points.Add(Point(
                point.MapId,
                point.StationId,
                point.StationName,
                inCurrentRegistration: true,
                enabled: point.Enabled,
                point.VehicleScope,
                holding,
                note: holding is not null && !point.Enabled ? DisabledStillHeld : null,
                contact));
        }
        foreach (StationExclusivityRow orphan in held.Where(row =>
                     registration?.Points.Any(point => point.MapId == row.MapId && point.StationId == row.StationId) != true))
        {
            points.Add(Point(
                orphan.MapId,
                orphan.StationId,
                stationName: null,
                inCurrentRegistration: false,
                enabled: null,
                vehicleScope: [],
                orphan,
                NotInCurrentRegistrationStillHeld,
                contact));
        }

        return new
        {
            registration = registration is null
                ? null
                : new
                {
                    version = registration.Version,
                    loadedAt = registration.LoadedAt,
                    contentSha256 = registration.ContentSha256,
                    snapshotId = registration.SnapshotId,
                    source = registration.Source,
                },
            points = points.ToArray(),
            unavailableVehicles = contact.Unavailable(),
        };
    }

    private static object Point(
        int mapId,
        int stationId,
        string? stationName,
        bool inCurrentRegistration,
        bool? enabled,
        IReadOnlyList<string> vehicleScope,
        StationExclusivityRow? holding,
        string? note,
        DashboardFleetContact contact) => new
        {
            mapId,
            stationId,
            stationName,
            inCurrentRegistration,
            enabled,
            // 白名单为空即同图全部车辆开放（REQ-0289）。
            vehicleScope = vehicleScope.ToArray(),
            // 没人持有而登记里停用了：停用。持有着的停用点状态照写预占或占用，停用写在 registrationNote 上。
            disabled = holding is null && enabled == false,
            registrationNote = note,
            registrationNoteDescription = note switch
            {
                DisabledStillHeld => "这个等待点在当前登记里已停用，但仍被下面这辆车预占或占用：停用只挡新的承诺，已有的要等离点证据满足才放",
                NotInCurrentRegistrationStillHeld =>
                    "这个等待点已不在当前登记里（登记换版时被删掉），但仍被下面这辆车预占或占用：要等离点证据满足才放",
                _ => null,
            },
            holding = StationHoldings.Project(holding, contact),
        };
}
