using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 车队视图「车辆用途」的数据面（批次8-21，control-server#392）：名册上每台车此刻的用途（<c>TRANSPORT</c>／<c>CHARGING</c>／
/// <c>CLEARING_MAINTENANCE</c>／<c>IDLE_RETURN</c>／无）、持有者（旅程、充电旅程或空闲返回记录）与取得时刻。
/// </summary>
/// <remarks>
/// 用途占有一车一行（<c>VehiclePurposeClaims</c>），这里原样投影，不判断它该不该在。听不到的车只进 <c>unavailableVehicles</c>：
/// 用途行虽然是服务端的记录，但「这辆车此刻在为谁干活」对一辆听不到的车说不准，按失联直述不给（REQ-0269）。
/// </remarks>
internal sealed class VehiclePurposesQueryEndpoint : IDashboardQueryEndpoint
{
    private readonly VehicleRoster _roster;
    private readonly TimeProvider _clock;

    public VehiclePurposesQueryEndpoint()
        : this(new VehicleRoster(Options.Create(new JourneyRuntimeOptions())), TimeProvider.System)
    {
    }

    /// <summary>
    /// 挂在宿主上时用这一个：名册从宿主的同一份配置建（<see cref="VehicleRoster"/> 只读配置、建好不变，与引擎那份单例逐项相同），
    /// 不要求宿主另外注册名册——看板的最小宿主只配了运行时选项。
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public VehiclePurposesQueryEndpoint(IOptions<JourneyRuntimeOptions> options)
        : this(new VehicleRoster(options), TimeProvider.System)
    {
    }

    internal VehiclePurposesQueryEndpoint(VehicleRoster roster, TimeProvider clock)
    {
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "vehicle-purposes";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        DashboardFleetContact contact =
            await DashboardFleetContact.ReadAsync(dbContext, _roster, _clock.GetUtcNow(), cancellationToken);
        Dictionary<string, VehiclePurposeClaimRow> claims = await VehiclePurposeFacts.ReadAsync(dbContext, cancellationToken);
        return new
        {
            vehicles = contact.Vehicles
                .Where(contact.InContact)
                .Select(vehicle => Fact(vehicle, claims.GetValueOrDefault(vehicle.VehicleKey)))
                .ToArray(),
            unavailableVehicles = contact.Unavailable(),
        };
    }

    private static object Fact(FleetVehicle vehicle, VehiclePurposeClaimRow? claim) => new
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
        claimedAt = claim?.ClaimedAt,
    };
}
