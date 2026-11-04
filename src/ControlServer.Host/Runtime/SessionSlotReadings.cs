using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 服务端手上一辆车几个仓此刻的读数，拿来做判定用（control-server#830）：「剩下的不装」的授权，与授权范围为空的装货中取消报 <c>ALL_EMPTY</c>
/// 而不带逐仓结果时的对账。
/// </summary>
/// <remarks>
/// <para>
/// <b>读的是什么。</b>这辆车当前会话（会话行的代次）里，<paramref name="notBefore"/> 之后收到的、<c>safetyStateVersion</c> 最大的那份
/// <c>SafetyStateSnapshot</c> 的逐仓状态。那份快照之后有 <c>SafetyStateChanged</c> 说某个目标仓变过，或者一份快照都没有，就是过期，返回 null——
/// <c>SafetyStateChanged</c> 只说哪几个仓变了、不带状态，变化之后的读数一律不用。真车载端每条变化的 <c>affectedSlots</c> 都是 1..8，
/// 所以实际上快照之后有任何变化都算过期。
/// </para>
/// <para>
/// <b>下限按这次装货开始的时刻。</b>调用方传这次装货操作创建的时刻：装货开始之前的快照说明不了装进了什么，读也白读；收件箱从不清理、心跳与安全
/// 消息只增不减，库里按 <c>(MessageType, ReceivedAtUtcTicks)</c> 索引先筛掉之前的行，不再整表读回来解析（审查意见）。两边都是服务端的时钟。
/// </para>
/// <para>
/// <b>安全基线不可信时一律没有读数</b>（control-server#837 审查 S2）。车报的安全消息被服务端拒收（同修订号内容不同、修订号倒退，control-server#478）时，
/// 那条消息不进收件箱，于是此前那份快照在这里仍像是最新、之后没有变化——而车手上已经是别的状态。拒收时 <c>DistrustSafetyBaselineAsync</c> 清掉会话行的
/// 出发安全结论（<c>DepartureSafe</c>），下一份被接受的快照才写回，所以它为空就返回 null，与过期同样处理。握手完成之前它也为空，那时本来就不判。
/// </para>
/// <para>
/// 与看板逾期卡片、人工判故障的审计读数（<c>SlotReadings</c>）同一个读法；那两处把快照之后的变化标出来给人看，这里要拿它做判定。
/// </para>
/// </remarks>
internal static class SessionSlotReadings
{
    public static async Task<IReadOnlyDictionary<int, RemainingBasketsSkipRule.SlotReading>?> ReadAsync(
        ControlServerDbContext dbContext,
        string agvId,
        IReadOnlyCollection<int> targets,
        DateTimeOffset notBefore,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(targets);
        var current = await dbContext.SessionRecoveries.AsNoTracking()
            .Where(row => row.AgvId == agvId)
            .Select(row => new { row.SessionGeneration, row.DepartureSafe })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (current?.DepartureSafe is null)
        {
            return null;
        }
        long generation = current.SessionGeneration;
        long notBeforeTicks = notBefore.UtcTicks;
        string[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => (row.MessageType == "SafetyStateSnapshot" || row.MessageType == "SafetyStateChanged") &&
                          row.ReceivedAtUtcTicks >= notBeforeTicks)
            .Select(row => row.RequestJson)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        JsonElement? latest = null;
        long latestVersion = long.MinValue;
        List<(long Version, int[] Slots)> changes = [];
        foreach (string json in rows)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("agvId", out JsonElement agv) || agv.GetString() != agvId ||
                !root.TryGetProperty("sessionGeneration", out JsonElement session) ||
                session.ValueKind != JsonValueKind.Number || session.GetInt64() != generation)
            {
                continue;
            }
            JsonElement payload = root.GetProperty("payload");
            long version = payload.GetProperty("safetyStateVersion").GetInt64();
            if (root.GetProperty("messageType").GetString() == "SafetyStateSnapshot")
            {
                if (version > latestVersion)
                {
                    latestVersion = version;
                    latest = payload.Clone();
                }
            }
            else if (payload.TryGetProperty("affectedSlots", out JsonElement affected) &&
                     affected.ValueKind == JsonValueKind.Array)
            {
                changes.Add((version, [.. affected.EnumerateArray().Select(item => item.GetInt32())]));
            }
        }
        if (latest is not JsonElement snapshot ||
            changes.Any(change => change.Version > latestVersion && change.Slots.Any(targets.Contains)))
        {
            return null;
        }
        Dictionary<int, RemainingBasketsSkipRule.SlotReading> readings = [];
        foreach (JsonElement state in snapshot.GetProperty("slotStates").EnumerateArray())
        {
            int slotNo = state.GetProperty("slotNo").GetInt32();
            if (targets.Contains(slotNo))
            {
                readings[slotNo] = new RemainingBasketsSkipRule.SlotReading(
                    state.GetProperty("physicalState").GetString()!,
                    state.GetProperty("lockState").GetString()!,
                    state.GetProperty("unlockOutputState").GetString()!);
            }
        }
        return readings;
    }
}
