using ControlServer.Application;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 等待点导入要判的库内事实（control-server#388）：任务类型绑定的固定站、服务端最近确认的目录修订。
/// </summary>
/// <remarks>
/// 固定站取生效版本与最新版本的并集：最新一版可能还没激活，登记一个马上就要成为固定站的点同样是 REQ-0289 说的共享角色。
/// </remarks>
public sealed class WaitingPointImportFacts(
    ITaskTypeStationBindingStore bindings,
    ICatalogAvailabilityStore catalogAvailability) : IWaitingPointImportFacts
{
    private readonly ITaskTypeStationBindingStore _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
    private readonly ICatalogAvailabilityStore _catalogAvailability =
        catalogAvailability ?? throw new ArgumentNullException(nameof(catalogAvailability));

    public async Task<IReadOnlySet<int>> ReadFixedTaskStationIdsAsync(int mapId, CancellationToken cancellationToken)
    {
        return WaitingPointFixedTaskStations.StationIds(
            await WaitingPointFixedTaskStations.ReadAsync(_bindings, mapId, cancellationToken));
    }

    public async Task<long?> ReadConfirmedCatalogRevisionAsync(int mapId, CancellationToken cancellationToken)
    {
        MapStationCatalogAvailability? state = await _catalogAvailability.ReadStateAsync(mapId, cancellationToken);
        return state?.LastCompleteConfirmationAt is null ? null : state.CatalogRevision;
    }
}
