using ControlServer.Application;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 导入每区派车参数时要判的库内事实（control-server#216）。
/// </summary>
/// <remarks>
/// 与分区归属表导入（<see cref="AreaAssignmentImportFacts"/>）取同一份：分区取 <c>DispatchZoneVehicles</c>，即服务端启动时按配置写入的
/// 调度策略。FieldOps 只开 SQLite、不读设置文件，所以导入前服务端须以目标配置启动过一次，没有车服务的分区判「不存在」。
/// </remarks>
public sealed class DispatchZoneParameterImportFacts(ControlServerDbContext context) : IDispatchZoneParameterImportFacts
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<IReadOnlySet<string>> ReadDispatchZonesAsync(CancellationToken cancellationToken)
    {
        string[] zones = await _context.DispatchZoneVehicles
            .AsNoTracking()
            .Select(row => row.Zone)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        return zones.ToHashSet(StringComparer.Ordinal);
    }
}
