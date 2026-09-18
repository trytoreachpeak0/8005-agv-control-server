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
/// **本类不实现协议 v2 的传输与序列化。**这里固化的是收到之后怎么存、怎么投影。消息 9
/// <c>OnboardAlarmSnapshot</c> 的收发在 <c>ControlServer.Host.Transport</c> 下的
/// <c>OnboardAlarmSnapshotWire</c> 与 <c>OnboardMessageProcessor</c>，它调用的是下面的
/// <see cref="RecordSnapshotAsync"/>，「序号回退的快照忽略掉」不在那边再判一次。
/// </para>
/// <para>
/// 告警码在这里始终是字符串，一次都不经过 <c>ErrorCode</c>：告警是开放集合，把它塞进封闭 enum 等于
/// 每加一个故障码就发一次 breaking change。
/// </para>
/// <para>本类不新增任何表。</para>
/// </remarks>
public sealed class OnboardAlarmProjectionStore(ControlServerDbContext context, TimeProvider? timeProvider = null)
{
    /// <summary>
    /// 多久听不到这一代会话的任何入站消息就算失联。与车队会话卡片共用 <see cref="SessionLiveness.Timeout"/>。
    /// </summary>
    public static readonly TimeSpan LinkLivenessTimeout = SessionLiveness.Timeout;

    private static readonly JsonSerializerOptions AlarmJson = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ControlServerDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// 收下一份快照。同一台车的后一份整体取代前一份。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 采纳判据是 <c>(会话代, 序号)</c> 这一对，按字典序比：新会话的快照无条件采纳，同一代之内序号
    /// 不前进才忽略——那是一份比库里更旧的东西，收下它等于让看板倒着走。
    /// </para>
    /// <para>
    /// **只按序号比是不够的。**车载端的告警板序号活在进程里，车一重启就从 1 重新开始；只按序号采纳
    /// 的话，重启后那台车的快照全被静默忽略，看板停在重启前那一批，而那正是 REQ-0269 禁止的不确定
    /// 新旧的旧值。车重启必然换一代会话，所以会话代把这个洞补上。
    /// </para>
    /// </remarks>
    public async Task RecordSnapshotAsync(
        OnboardAlarmSnapshotView snapshot,
        long sessionGeneration,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.AgvId);
        ArgumentOutOfRangeException.ThrowIfNegative(sessionGeneration);

        OnboardAlarmSnapshotRow? existing = await _context.Set<OnboardAlarmSnapshotRow>()
            .FirstOrDefaultAsync(row => row.AgvId == snapshot.AgvId, cancellationToken);
        if (existing is not null && !Advances(existing, sessionGeneration, snapshot.SnapshotSequence))
        {
            return;
        }

        string alarmsJson = JsonSerializer.Serialize(snapshot.Alarms, AlarmJson);
        if (existing is null)
        {
            _context.Set<OnboardAlarmSnapshotRow>().Add(new OnboardAlarmSnapshotRow
            {
                AgvId = snapshot.AgvId,
                SessionGeneration = sessionGeneration,
                SnapshotSequence = snapshot.SnapshotSequence,
                CapturedAt = snapshot.CapturedAt,
                ReceivedAt = receivedAt,
                AlarmsJson = alarmsJson
            });
        }
        else
        {
            // 整体取代：这几个字段一起换成新快照的，不合并、不保留上一份里多出来的那几条。
            existing.SessionGeneration = sessionGeneration;
            existing.SnapshotSequence = snapshot.SnapshotSequence;
            existing.CapturedAt = snapshot.CapturedAt;
            existing.ReceivedAt = receivedAt;
            existing.AlarmsJson = alarmsJson;
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// 这台车库里那一份快照里，此刻挂着期待动作超时的那些告警（REQ-0358）。没有快照时为空。
    /// </summary>
    /// <remarks>
    /// 读的是库里的那一份，不问车在不在线：Host 用它判断「这一份快照里的超时是不是新出现的」「这次安全变化涉及的是不是超时仓」，
    /// 两处都在收到这台车的消息时调用，车显然在线。看板那一侧的在线判定仍在 <see cref="ReadDashboardProjectionAsync"/>。
    /// </remarks>
    public async Task<IReadOnlyList<OnboardAlarmEntry>> ReadExpectedActionOverdueAsync(
        string agvId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);

        string? alarmsJson = await _context.Set<OnboardAlarmSnapshotRow>().AsNoTracking()
            .Where(row => row.AgvId == agvId)
            .Select(row => row.AlarmsJson)
            .FirstOrDefaultAsync(cancellationToken);
        if (alarmsJson is null)
        {
            return [];
        }
        OnboardAlarmEntry[] alarms = JsonSerializer.Deserialize<OnboardAlarmEntry[]>(alarmsJson, AlarmJson) ?? [];
        return [.. alarms.Where(OnboardAlarmCodes.IsSlotExpectedActionOverdue)];
    }

    /// <summary>
    /// 这一份是不是比库里那一份更新——<c>(会话代, 序号)</c> 按字典序比。
    /// </summary>
    private static bool Advances(OnboardAlarmSnapshotRow existing, long sessionGeneration, long sequence) =>
        sessionGeneration > existing.SessionGeneration
        || (sessionGeneration == existing.SessionGeneration && sequence > existing.SnapshotSequence);

    /// <summary>
    /// 看板这一轮该显示什么：每台车要么是当下的全部告警（REQ-0270），要么是拿不到它的原因。
    /// </summary>
    /// <remarks>
    /// 失联的车显示「失联」，不显示它失联前的最后一批告警——REQ-0269 的失联直述在这里就是这一句。
    /// 判定失联用服务端自己手上的会话事实，不问车，见 <see cref="LinkedVehiclesAsync"/>。
    /// </remarks>
    public async Task<IReadOnlyList<VehicleAlarmProjection>> ReadDashboardProjectionAsync(
        CancellationToken cancellationToken)
    {
        OnboardAlarmSnapshotRow[] snapshots = await _context.Set<OnboardAlarmSnapshotRow>().AsNoTracking()
            .ToArrayAsync(cancellationToken);
        HashSet<string> linked = await LinkedVehiclesAsync(cancellationToken);

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

    /// <summary>
    /// 此刻算在线的车：会话行是 Ready，**并且**服务端在 <see cref="LinkLivenessTimeout"/> 之内收到过这一代
    /// 会话的任何入站消息。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **只看 Ready 是不够的。**车断线时服务端只把连接从 <c>OnboardPeer</c> 上摘掉，落库的会话行原样留着
    /// Ready——恢复握手靠的正是这一行，断线不改它是对的。但这意味着一台死掉的车在库里一直是 Ready，只看这一列
    /// 的看板会一直显示它失联前的最后一批告警，那正是 REQ-0269 禁止的东西。L2 场景
    /// <c>onboard-alarm-snapshot-dashboard</c> 第一次跑就撞上了，见
    /// <c>docs/defects/20260910-dashboard-kept-showing-a-dead-vehicles-last-alarms.md</c>。
    /// </para>
    /// <para>
    /// 与 <c>JourneyRuntimeEngine</c> 派车前的存活判定是同一条规则：取服务端收件时间而不取载荷时间，一个停走
    /// 或配错的车载时钟不能让死会话看起来活着；任何入站消息都算；收件时间晚于此刻的不算；只认会话行上那一代，
    /// 上一代最后那条心跳再新也不替这一代说话。
    /// </para>
    /// <para>
    /// 代价与引擎那一处相同：agvId 与会话代只在信封 JSON 里，所以要扫一遍收件箱。只有在存活窗口之内的行才被
    /// 解析。
    /// </para>
    /// </remarks>
    private async Task<HashSet<string>> LinkedVehiclesAsync(CancellationToken cancellationToken)
    {
        HashSet<string> heard = await SessionLiveness.HeardFromAsync(
            _context, _timeProvider.GetUtcNow(), cancellationToken);
        HashSet<string> linked = new(
            await _context.SessionRecoveries.AsNoTracking()
                .Where(row => row.Readiness == SessionReadiness.Ready)
                .Select(row => row.AgvId)
                .ToArrayAsync(cancellationToken),
            StringComparer.Ordinal);
        linked.IntersectWith(heard);
        return linked;
    }
}
