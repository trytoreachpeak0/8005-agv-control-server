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
/// <b>超时层读批次7-09 落库的告警标记</b>（<c>StarvationEscalatedAt</c>），不按阈值重算：派车轮进入超时层的那一刻就写下它，而且只写一次，
/// 所以「看板说它在超时层」与「它告过警」按构造是同一件事。标记之后年龄只会变大，同一版阈值下它不会自己退出超时层。
/// 两种情形这里不再把它排进超时层：它属于最高带（最高带从不升级，标记不可能出现；出现了就是库坏了，按带显示），
/// 以及当前没有任何分区批准了阈值（REQ-0203：未批准只计龄、不跨带升级——把阈值撤回之后，派车轮也不再把它排进超时层）。
/// </para>
/// <para>
/// <b>优先级带从业务键取任务类型。</b>积压行没有任务类型列（本票零迁移），而业务键是服务端自己拼的 <c>{sublot}|{workType}</c>
/// （<c>HttpMesIngestCatalog</c>），取最后一个 <c>|</c> 之后那一段。这个拼法一改，这里就会把 <c>STAGING_TO_WIRE</c> 显示成普通带——
/// <c>Batch7CargoHoldingDashboardTests</c> 用真实的拼法钉着。
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

    internal static bool InTopBand(JourneyBacklogRow row)
    {
        int separator = row.TransportDemandKey.LastIndexOf('|');
        return separator >= 0 &&
               string.Equals(row.TransportDemandKey[(separator + 1)..], TransportTaskTypes.StagingToWire, StringComparison.Ordinal);
    }

    /// <summary>桩（批次7-12 调度返工，测试先行）。</summary>
    internal static BacklogTier TierOf(JourneyBacklogRow row) => throw new NotImplementedException();

    internal static BacklogTier TierOf(JourneyBacklogRow row, StarvationThresholds thresholds)
    {
        if (InTopBand(row))
        {
            return BacklogTier.TopBand;
        }
        return row.StarvationEscalatedAt is not null && thresholds.AnyApproved
            ? BacklogTier.StarvationTimeout
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
