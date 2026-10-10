using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 看板上「一趟旅程带着哪些需求、各自到了哪一步」的唯一读法（批次7-12，control-server#217）：持货等单与旅程阻断两张卡片共用。
/// </summary>
/// <remarks>
/// <para>
/// <b>只列未移除的归属</b>（<see cref="DemandJourneyLookup.Memberships"/>）。批次7-10（control-server#215）之后，一条需求被释放改派，
/// 它在旧旅程上的归属只标移除、不删行，而同一条需求会在新旅程上再有一条归属——按旅程读、只读未移除的，它就恰好出现在它现在所在的那一趟上。
/// 按 <c>JourneyRuntimes.DemandId</c>（锚需求）去找旅程不再唯一：同一个锚可以有两行旅程（旧的已完成，新的在跑），所以这里一律按旅程 id。
/// </para>
/// <para>
/// 状态取归属行（<see cref="JourneyDemandRow.Status"/>），不取需求的执行状态：卸货结果刚落定那一刻需求已经成功而归属还停在已装，
/// 看板照推进段的口径显示。
/// </para>
/// </remarks>
internal sealed class JourneyDemandList
{
    private static readonly IReadOnlyList<JourneyDemandRow> None = [];

    private readonly ILookup<string, JourneyDemandRow> _memberships;
    private readonly Dictionary<string, string> _keys;

    private JourneyDemandList(ILookup<string, JourneyDemandRow> memberships, Dictionary<string, string> keys)
    {
        _memberships = memberships;
        _keys = keys;
    }

    internal static async Task<JourneyDemandList> ReadAsync(
        ControlServerDbContext dbContext,
        IReadOnlyCollection<string> journeyIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(journeyIds);
        JourneyDemandRow[] memberships = await DemandJourneyLookup.Memberships(dbContext).AsNoTracking()
            .Where(row => journeyIds.Contains(row.JourneyId))
            .ToArrayAsync(cancellationToken);
        string[] demandIds = [.. memberships.Select(row => row.DemandId).Distinct(StringComparer.Ordinal)];
        Dictionary<string, string> keys = await dbContext.AcceptedDemands.AsNoTracking()
            .Where(row => demandIds.Contains(row.DemandId))
            .ToDictionaryAsync(row => row.DemandId, row => row.TransportDemandKey, StringComparer.Ordinal, cancellationToken);
        return new JourneyDemandList(memberships.ToLookup(row => row.JourneyId, StringComparer.Ordinal), keys);
    }

    /// <summary>这趟旅程未移除的归属，按加入旅程的先后、同一刻再按需求 id；排序在内存里做（SQLite 不能按 DateTimeOffset 排序）。</summary>
    internal IReadOnlyList<JourneyDemandRow> MembershipsOf(string journeyId) =>
        _memberships.Contains(journeyId)
            ? [.. _memberships[journeyId].OrderBy(row => row.AddedAt).ThenBy(row => row.DemandId, StringComparer.Ordinal)]
            : None;

    /// <summary>
    /// 看板 JSON 里的 <c>demands</c>。业务键是左连接取的：受理事务把需求与归属写在同一次保存里，正常库里一条不少；
    /// 少了也不让整张卡片读挂，业务键给空。
    /// </summary>
    internal object[] FactsOf(string journeyId) =>
        [.. MembershipsOf(journeyId).Select(row => (object)new
        {
            demandId = row.DemandId,
            transportDemandKey = _keys.GetValueOrDefault(row.DemandId),
            status = row.Status
        })];
}
