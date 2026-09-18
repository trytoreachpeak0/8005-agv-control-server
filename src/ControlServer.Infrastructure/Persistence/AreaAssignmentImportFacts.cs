using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 导入分区归属表时要判的那几份库内事实（control-server#68）。
/// </summary>
/// <remarks>
/// 分区取 <c>DispatchZoneVehicles</c>，不读 <c>appsettings.json</c>：FieldOps 的设计是只开 SQLite、不读设置
/// 文件，库内调度策略是它能拿到的唯一权威。代价是导入前服务端须以目标配置启动过一次，且配置了分区却没有车
/// 服务的分区会被判「不存在」——两条都写进了工具的用法文本。
/// </remarks>
public sealed class AreaAssignmentImportFacts(ControlServerDbContext context) : IAreaAssignmentImportFacts
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<IReadOnlySet<string>> ReadPublishedSlotPositionGroupsAsync(CancellationToken cancellationToken)
    {
        string[] groups = await _context.Set<SlotModelVersionRow>()
            .AsNoTracking()
            .Where(model => model.Status == SlotConfigurationVersionLine.PublishedStatus)
            .Join(
                _context.Set<SlotModelSlotRow>().AsNoTracking(),
                model => model.SlotModelVersionId,
                slot => slot.SlotModelVersionId,
                (model, slot) => slot.SlotPosition)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        return groups.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlySet<string>> ReadDispatchZonesAsync(CancellationToken cancellationToken)
    {
        string[] zones = await _context.DispatchZoneVehicles
            .AsNoTracking()
            .Select(row => row.Zone)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        return zones.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadDemandAreasAsync(
        IReadOnlyCollection<string> demandIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(demandIds);
        if (demandIds.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        string[] wanted = [.. demandIds];
        var rows = await _context.Set<AcceptedDemandRow>()
            .AsNoTracking()
            .Where(row => wanted.Contains(row.DemandId))
            .Select(row => new { row.DemandId, row.LiveMesFieldsJson })
            .ToArrayAsync(cancellationToken);

        Dictionary<string, string> areas = new(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            string? area = JsonSerializer.Deserialize<LiveMesFieldSet>(row.LiveMesFieldsJson)?.Area;
            if (!string.IsNullOrWhiteSpace(area))
            {
                areas[row.DemandId] = area;
            }
        }
        return areas;
    }
}
