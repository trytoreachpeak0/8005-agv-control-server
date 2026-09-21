using ControlServer.Application;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Dashboard;

/// <summary>积压卡片上一条需求所在的层，按派车排序的层序（<see cref="DispatchCandidateOrdering"/>）从前到后。</summary>
internal enum BacklogTier
{
    StarvationTimeout = 0,
    TopBand = 1,
    NormalBand = 2,
}

/// <summary>
/// 积压卡片上一条需求的层与等待年龄（批次7-12，control-server#217；REQ-0201、REQ-0202、REQ-0203），全部从积压行读，不重算派车轮的判断。
/// </summary>
/// <remarks>
/// <para>
/// <b>等待年龄从需求的建单时刻起算</b>（积压行 <c>DemandCreatedAt</c>，即 MesIngest 的 <c>CreatedAt</c>），与派车排序、防饥饿告警同一个起点
/// （<see cref="TaskStarvation.WaitingAge"/>，批次7-09）：负钟差夹到 0，建单时刻为默认值时读作「不知道」、年龄按 0 算。
/// 看板从不重置它——离开目录再回来，积压行还是那一行，建单时刻还是那一刻。
/// </para>
/// <para>
/// <b>超时层只读派车记下的事实</b>：批次7-09 落库的告警标记 <c>StarvationEscalatedAt</c> 非空就在超时层，不叠加任何现算的条件
/// （调度 2026-09-22）。派车轮进入超时层的那一刻写下它、只写一次，所以「看板说它在超时层」与「它告过警」按构造是同一件事；
/// 排序用的参数版本不落库（7-09 审查已接受），看板能依据的也只有这个标记。阈值后来撤回了，标记照样显示，卡片另加一句说明
/// （<c>DispatchBacklogCard</c>），而不是改判定。7-05 把被抑制与已受理的键排除在超时层外，那也只影响写不写标记，这里不受影响。
/// </para>
/// <para>
/// <b>优先级带与派车排序是同一个判断</b>：<see cref="TaskStarvation.IsTopBandWorkType"/>。积压行没有任务类型列（批次 7 零迁移），
/// 任务类型从业务键读回（<see cref="TransportDemandKeys.WorkTypeOf"/>，与 <c>HttpMesIngestCatalog</c> 拼它的是同一处）。
/// </para>
/// </remarks>
internal static class BacklogStanding
{
    internal static bool HasLocalCreation(JourneyBacklogRow row) => row.DemandCreatedAt != default;

    internal static TimeSpan WaitingAge(JourneyBacklogRow row, DateTimeOffset now)
    {
        if (!HasLocalCreation(row))
        {
            return TimeSpan.Zero;
        }
        TimeSpan age = now - row.DemandCreatedAt;
        return age > TimeSpan.Zero ? age : TimeSpan.Zero;
    }

    internal static BacklogTier TierOf(JourneyBacklogRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.StarvationEscalatedAt is not null)
        {
            return BacklogTier.StarvationTimeout;
        }
        return TaskStarvation.IsTopBandWorkType(TransportDemandKeys.WorkTypeOf(row.TransportDemandKey))
            ? BacklogTier.TopBand
            : BacklogTier.NormalBand;
    }
}

/// <summary>
/// 当前每区派车参数版本里各分区的防饥饿阈值（REQ-0203），加上分区归属表里有、参数表里没有的分区（未配置即未批准）。
/// </summary>
/// <remarks>
/// 积压行不带 AREA，看板说不出每条需求属于哪个分区，所以「本区阈值未批准」按卡片整体说：一个分区都没有批准时整张卡片写明只计龄，
/// 部分批准时列出每个分区的阈值。
/// </remarks>
internal sealed class StarvationThresholds
{
    private StarvationThresholds(IReadOnlyDictionary<string, long?> zones)
    {
        Zones = zones;
    }

    /// <summary>分区 → 阈值秒数；空即未配置（未批准）。按分区名排序。</summary>
    internal IReadOnlyDictionary<string, long?> Zones { get; }

    internal bool AnyApproved => Zones.Values.Any(threshold => threshold is not null);

    internal static async Task<StarvationThresholds> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        SortedDictionary<string, long?> zones = new(StringComparer.Ordinal);
        long? areaVersion = await dbContext.Set<DispatchZoneAreaAssignmentVersionRow>()
            .MaxAsync(row => (long?)row.Version, cancellationToken);
        if (areaVersion is { } areas)
        {
            foreach (string zone in await dbContext.Set<DispatchZoneAreaAssignmentRow>().AsNoTracking()
                         .Where(row => row.Version == areas)
                         .Select(row => row.DispatchZone)
                         .Distinct()
                         .ToArrayAsync(cancellationToken))
            {
                zones[zone] = null;
            }
        }
        long? parameterVersion = await dbContext.Set<DispatchZoneParameterVersionRow>()
            .MaxAsync(row => (long?)row.Version, cancellationToken);
        if (parameterVersion is { } parameters)
        {
            foreach (DispatchZoneParameterRow row in await dbContext.Set<DispatchZoneParameterRow>().AsNoTracking()
                         .Where(row => row.Version == parameters)
                         .ToArrayAsync(cancellationToken))
            {
                zones[row.DispatchZone] = row.StarvationThresholdSeconds;
            }
        }
        return new StarvationThresholds(zones);
    }
}
