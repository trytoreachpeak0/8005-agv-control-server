using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 照样计龄、但不进超时层、不做防饥饿升级的需求（批次7-09 定下这一类，批次7-05 扩充；control-server#214、#210）。
/// </summary>
/// <remarks>
/// <para>
/// <b>判别标准是「它连合格候选都不是」。</b>超时层只排判据全过的候选；一条永远不会被任何车接走的需求在那里告一次饥饿，
/// 说的是「排队不公平」，而它的真实处境是「有意不执行」或「配置错了」——前者没人需要处理，后者已经有自己的告警。
/// 今天有三种：
/// </para>
/// <list type="bullet">
/// <item>有未解除结构性派车阻断的（批次7-09，REQ-0210）；</item>
/// <item>业务键已被本地取消抑制的（<see cref="DispatchReasonCodes.TransportDemandKeySuppressed"/>，REQ-0155）；</item>
/// <item>业务键已有别的 <c>DemandId</c> 被受理过的（<see cref="DispatchReasonCodes.TransportDemandKeyAlreadyAccepted"/>）。</item>
/// </list>
/// <para>
/// 后两种在现场一定会出现：REQ-0155 描述的正是 MesIngest 持续可见、不断给同一个键发新 <c>DemandId</c> 的情形，而这种新需求的
/// 本地建单时刻常常很早——不排除的话它一越过阈值就记一条 2161、此后每轮排在超时层最前面。
/// </para>
/// <para>
/// <b>一处算、两处用</b>：派车轮排序（开轮时）与 <see cref="StarvationEscalationSink"/>（轮末）都经这里，只是调用时刻不同。
/// 两边各查一遍的话，下一个往这个集合里加一类的人只会改到一边，排序与告警就对「是否超时」给出两个答案。
/// 后两种的判法与 <see cref="Criteria.TransportDemandKeySuppressedCriterion"/>、
/// <see cref="Criteria.TransportDemandKeyAlreadyAcceptedCriterion"/> 相同（同键有抑制；同键有 <c>DemandId</c> 不同的受理行），
/// 只是按整份目录一次查完：两道判据逐条候选、逐车查，这里一轮两次。
/// </para>
/// </remarks>
public static class StarvationExclusions
{
    /// <summary><paramref name="catalog"/> 里此刻不进超时层的需求 id，加上全部未解除结构性阻断的需求 id。</summary>
    public static async Task<HashSet<string>> ReadAsync(
        ControlServerDbContext dbContext,
        IReadOnlyCollection<AcceptedDemandSnapshot> catalog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(catalog);

        HashSet<string> excluded = (await dbContext.Set<StructuralDispatchBlockRow>().AsNoTracking()
                .Where(row => row.ClearedAt == null)
                .Select(row => row.DemandId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        if (catalog.Count == 0)
        {
            return excluded;
        }

        string[] keys = [.. catalog.Select(demand => demand.TransportDemandKey).Distinct(StringComparer.Ordinal)];
        HashSet<string> suppressedKeys = (await dbContext.Set<TransportDemandSuppressionRow>().AsNoTracking()
                .Where(row => keys.Contains(row.TransportDemandKey))
                .Select(row => row.TransportDemandKey)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        ILookup<string, string> acceptedByKey = (await dbContext.AcceptedDemands.AsNoTracking()
                .Where(row => keys.Contains(row.TransportDemandKey))
                .Select(row => new { row.TransportDemandKey, row.DemandId })
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(row => row.TransportDemandKey, row => row.DemandId, StringComparer.Ordinal);

        foreach (AcceptedDemandSnapshot demand in catalog)
        {
            if (suppressedKeys.Contains(demand.TransportDemandKey) ||
                acceptedByKey[demand.TransportDemandKey].Any(id => !string.Equals(id, demand.DemandId, StringComparison.Ordinal)))
            {
                excluded.Add(demand.DemandId);
            }
        }
        return excluded;
    }
}
