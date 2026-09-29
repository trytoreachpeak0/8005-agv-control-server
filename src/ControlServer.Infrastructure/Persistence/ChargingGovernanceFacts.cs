using ControlServer.Application;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 名册导入与策略导入、批准、激活要判的库内事实（control-server#400）：任务类型固定站、进行中的充电周期、策略的最新版本号与激活。
/// 只读，不写任何行；表来自 control-server#399。
/// </summary>
public sealed class ChargingGovernanceFacts(
    ControlServerDbContext context,
    ITaskTypeStationBindingStore bindings) : IChargerRosterImportFacts, IChargingPolicyGovernanceFacts
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly ITaskTypeStationBindingStore _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));

    public async Task<IReadOnlySet<int>> ReadFixedTaskStationIdsAsync(int mapId, CancellationToken cancellationToken) =>
        WaitingPointFixedTaskStations.StationIds(
            await WaitingPointFixedTaskStations.ReadAsync(_bindings, mapId, cancellationToken));

    public async Task<IReadOnlyList<ChargingCycle>> ListOpenCyclesAsync(CancellationToken cancellationToken)
    {
        // Read through the cycle store so there is one mapping from row to model; a vehicle has at most one open cycle.
        string[] vehicles = await _context.Set<ChargingCycleRow>().AsNoTracking()
            .Where(row => row.Phase != ChargingCyclePhases.Ended)
            .Select(row => row.VehicleKey)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        ChargingCycleStore cycles = new(_context);
        List<ChargingCycle> open = [];
        foreach (string vehicleKey in vehicles)
        {
            if (await cycles.ReadOpenAsync(vehicleKey, cancellationToken) is ChargingCycle cycle)
            {
                open.Add(cycle);
            }
        }
        // Ordered in memory: SQLite cannot order by a DateTimeOffset column.
        return [.. open.OrderBy(cycle => cycle.AllocatedAt).ThenBy(cycle => cycle.VehicleKey, StringComparer.Ordinal)];
    }

    public async Task<long?> ReadLatestVersionNumberAsync(CancellationToken cancellationToken) =>
        await _context.Set<ChargingPolicyVersionRow>().MaxAsync(row => (long?)row.Version, cancellationToken);

    public async Task<ChargingPolicyActivation?> ReadLatestActivationAsync(CancellationToken cancellationToken)
    {
        ChargingPolicyActivationRow? row = await _context.Set<ChargingPolicyActivationRow>().AsNoTracking()
            .OrderByDescending(item => item.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : ToModel(row);
    }

    public async Task<IReadOnlyList<ChargingPolicyActivation>> ListActivationsAsync(CancellationToken cancellationToken)
    {
        ChargingPolicyActivationRow[] rows = await _context.Set<ChargingPolicyActivationRow>().AsNoTracking()
            .OrderBy(item => item.Sequence)
            .ToArrayAsync(cancellationToken);
        return [.. rows.Select(ToModel)];
    }

    private static ChargingPolicyActivation ToModel(ChargingPolicyActivationRow row) =>
        new(row.ActivationId, row.Sequence, row.Version, row.ActivatedAt, row.ActivatedBy);
}
