using ControlServer.Application;
using ControlServer.Domain;
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
/// 积压卡片上一条需求的层、次序与等待年龄（批次7-12，control-server#217；REQ-0201、REQ-0202、REQ-0203）：把积压行还原成派车轮里的那条任务
/// （<see cref="DispatchTask"/>），然后全部交给派车自己的函数——看板不另写一套判定（调度 2026-09-22、审查 M1／M2／L1）。
/// </summary>
/// <remarks>
/// <para>
/// <b>还原出来的任务只带派车排序读的那几样</b>：需求 id、业务键、任务类型、建单时刻（积压行 <c>DemandCreatedAt</c>，即 MesIngest 的
/// <c>CreatedAt</c>）、首次看到，以及超时层。次序交给 <see cref="DispatchCandidateOrdering.Ranker"/>，带交给
/// <see cref="TaskStarvation.InTopBand"/>，等待年龄与「建单时刻知不知道」交给 <see cref="TaskStarvation.WaitingAge"/>、
/// <see cref="TaskStarvation.HasLocalCreation"/>——与派车排序、防饥饿告警同一个起点，负钟差夹 0。
/// </para>
/// <para>
/// <b>任务类型从业务键读回</b>（<see cref="TransportDemandKeys.WorkTypeOf"/>）：积压行没有任务类型列（批次 7 零迁移），而业务键是
/// <c>HttpMesIngestCatalog</c> 用 <see cref="TransportDemandKeys.Compose"/> 拼的，拼与拆在同一处。
/// </para>
/// <para>
/// <b>超时层只读派车记下的事实</b>：批次7-09 落库的告警标记 <c>StarvationEscalatedAt</c> 非空就在超时层，不叠加任何现算的条件。
/// 派车轮进入超时层的那一刻写下它、只写一次；排序用的参数版本不落库（7-09 审查已接受），看板能依据的只有这个标记。阈值后来撤回了，
/// 标记照样显示，卡片另加一句说明（<c>DispatchBacklogCard</c>），而不是改判定。7-05 把被抑制与已受理的键排除在超时层外，
/// 那只影响写不写标记，这里不受影响。
/// </para>
/// </remarks>
internal static class BacklogStanding
{
    internal static DispatchTask TaskOf(JourneyBacklogRow row, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row);
        AcceptedDemandSnapshot snapshot = new(
            row.DemandId,
            row.TransportDemandKey,
            DemandRevision: 0,
            HistoryEpoch: string.Empty,
            CatalogRevision: 0,
            AcceptedAt: default,
            WorkType: TransportDemandKeys.WorkTypeOf(row.TransportDemandKey) ?? string.Empty,
            CreatedAt: row.DemandCreatedAt);
        return new DispatchTask(snapshot, row.FirstSeenAt)
        {
            Starvation = row.StarvationEscalatedAt is null
                ? null
                : new TaskStarvationStanding(
                    TaskStarvation.WaitingAge(snapshot, now),
                    DispatchZone: null,
                    ThresholdSeconds: null,
                    ParameterVersion: row.StarvationEscalationParameterVersion,
                    Overdue: true)
        };
    }

    internal static BacklogTier TierOf(JourneyBacklogRow row) => TierOf(TaskOf(row, default));

    internal static BacklogTier TierOf(DispatchTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Starvation?.Overdue == true)
        {
            return BacklogTier.StarvationTimeout;
        }
        return TaskStarvation.InTopBand(task.Snapshot) ? BacklogTier.TopBand : BacklogTier.NormalBand;
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
