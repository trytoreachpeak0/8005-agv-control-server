using System.Text.Json;
using System.Text.Json.Serialization;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 收下车载端的告警快照，投影成看板读的那一份（REQ-0270 服务端半边、REQ-0269）。
/// </summary>
/// <remarks>
/// <para>
/// **一车一行，后一份整体取代前一份。**不做增量合并——快照的全部意义就在这里：重连之后不需要知道
/// 断线期间漏了什么，因为这一份就是当下的全部事实。
/// </para>
/// <para>
/// **本类不实现协议 v2 的传输与序列化。**消息 9 <c>OnboardAlarmSnapshot</c> 的收发属批次 2 轨 A，
/// 尚未落地；这里固化的是收到之后怎么存、怎么投影。
/// </para>
/// <para>
/// 告警码在这里始终是字符串，一次都不经过 <c>ErrorCode</c>：告警是开放集合，把它塞进封闭 enum 等于
/// 每加一个故障码就发一次 breaking change。
/// </para>
/// <para>本类不新增任何表。</para>
/// </remarks>
public sealed class OnboardAlarmProjectionStore(ControlServerDbContext context)
{
    private static readonly JsonSerializerOptions AlarmJson = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ControlServerDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>
    /// 收下一份快照。同一台车的后一份整体取代前一份。
    /// </summary>
    /// <remarks>
    /// 序号回退的快照被忽略：那是一份比库里更旧的东西，收下它等于让看板倒着走。
    /// </remarks>
    public async Task RecordSnapshotAsync(
        OnboardAlarmSnapshotView snapshot,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.AgvId);

        OnboardAlarmSnapshotRow? existing = await _context.Set<OnboardAlarmSnapshotRow>()
            .FirstOrDefaultAsync(row => row.AgvId == snapshot.AgvId, cancellationToken);
        if (existing is not null && existing.SnapshotSequence >= snapshot.SnapshotSequence)
        {
            return;
        }

        string alarmsJson = JsonSerializer.Serialize(snapshot.Alarms, AlarmJson);
        if (existing is null)
        {
            _context.Set<OnboardAlarmSnapshotRow>().Add(new OnboardAlarmSnapshotRow
            {
                AgvId = snapshot.AgvId,
                SnapshotSequence = snapshot.SnapshotSequence,
                CapturedAt = snapshot.CapturedAt,
                ReceivedAt = receivedAt,
                AlarmsJson = alarmsJson
            });
        }
        else
        {
            // 整体取代：这三个字段一起换成新快照的，不合并、不保留上一份里多出来的那几条。
            existing.SnapshotSequence = snapshot.SnapshotSequence;
            existing.CapturedAt = snapshot.CapturedAt;
            existing.ReceivedAt = receivedAt;
            existing.AlarmsJson = alarmsJson;
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// 看板这一轮该显示什么：每台车要么是当下进看板的告警，要么是拿不到它的原因。
    /// </summary>
    /// <remarks>
    /// 失联的车显示「失联」，不显示它失联前的最后一批告警——REQ-0269 的失联直述在这里就是这一句。
    /// 判定失联用服务端自己手上的会话事实，不问车。
    /// </remarks>
    public async Task<IReadOnlyList<VehicleAlarmProjection>> ReadDashboardProjectionAsync(
        CancellationToken cancellationToken)
    {
        OnboardAlarmSnapshotRow[] snapshots = await _context.Set<OnboardAlarmSnapshotRow>().AsNoTracking()
            .ToArrayAsync(cancellationToken);
        HashSet<string> linked = [.. await _context.SessionRecoveries.AsNoTracking()
            .Where(row => row.Readiness == SessionReadiness.Ready)
            .Select(row => row.AgvId)
            .ToArrayAsync(cancellationToken)];

        List<VehicleAlarmProjection> projections = [];
        foreach (string agvId in snapshots.Select(row => row.AgvId).Union(linked).Order(StringComparer.Ordinal))
        {
            OnboardAlarmSnapshotRow? row = Array.Find(snapshots, candidate => candidate.AgvId == agvId);
            if (!linked.Contains(agvId))
            {
                projections.Add(new VehicleAlarmProjection(
                    agvId, row?.SnapshotSequence ?? 0, null, [], VehicleAlarmProjection.LinkDownReason));
                continue;
            }
            if (row is null)
            {
                projections.Add(new VehicleAlarmProjection(
                    agvId, 0, null, [], VehicleAlarmProjection.NeverReportedReason));
                continue;
            }
            OnboardAlarmSnapshotView view = new(
                row.AgvId,
                row.SnapshotSequence,
                row.CapturedAt,
                JsonSerializer.Deserialize<OnboardAlarmEntry[]>(row.AlarmsJson, AlarmJson) ?? []);
            projections.Add(new VehicleAlarmProjection(
                agvId,
                row.SnapshotSequence,
                row.CapturedAt,
                OnboardAlarmDashboardVisibility.ForDashboard(view),
                null));
        }
        return projections;
    }
}
