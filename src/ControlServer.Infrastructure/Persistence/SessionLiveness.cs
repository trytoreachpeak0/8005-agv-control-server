using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 服务端自己观测到的会话存活：多久之内收到过某台车**当前这一代**会话的任何入站消息。
/// </summary>
/// <remarks>
/// <para>
/// 车断线时服务端只把连接从 <c>OnboardPeer</c> 上摘掉，落库的会话行原样留着——恢复握手靠的正是那一行，断线
/// 不改它是对的。代价是会话行上的 <c>Readiness</c> 说不了「车现在在不在」：一台死掉的车在库里一直是它最后一次
/// 在线时的判定。凡是要说「在线」的地方都经过这里，不各自再判一次——车载告警卡片 2026-09-10 就是因为只看
/// 那一列，断线六十秒后仍显示失联前的告警（<c>docs/defects/20260910-dashboard-kept-showing-a-dead-vehicles-last-alarms.md</c>）。
/// </para>
/// <para>
/// <b>四个消费者，两类分量，调这个值之前先看清楚是哪一类（control-server#234）。</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>只影响显示</b>：车队会话卡片（<c>FleetSessionsQueryEndpoint</c>）与车载告警投影
/// （<c>OnboardAlarmProjectionStore</c>）——听不到就不报那台车的就绪与告警。
/// </description></item>
/// <item><description>
/// <b>改变系统行为</b>：会话层的静默失联判定（<c>OnboardTcpServer</c>，超时即主动关闭这条连接）与在途旅程的
/// 阻断码（<c>JourneyRuntimeEngine</c> 的 <c>ONBOARD_SESSION_LOST</c>，让看板阻断卡片列出这条旅程）。
/// 把这个值调大，一台卡死的车会更久无人知晓；调小，正常车会被误判、连接被关。
/// </description></item>
/// </list>
/// <para>
/// 判定规则四条，由 <see cref="InsideWindow"/> 与 <see cref="Identify"/> 两个共用谓词表达，批量版与单车版都走
/// 它们：取服务端收件时间，不取载荷时间——一个停走或配错的车载时钟不能让死会话看起来活着；任何入站消息都算，
/// 心跳两秒一次；收件时间晚于此刻的不算；只认会话行上那一代，上一代最后那条心跳再新也不替这一代说话。
/// </para>
/// <para>
/// <b>这个窗口是数据库口径，与会话层关连接用的单调时钟阈值同源而判定时钟不同源，那是有意的。</b>
/// 会话层按 ADR-cross-0027 用本机单调时钟计时，这里用收件时间与当前 UTC 相减；两者只在系统时钟被调整时分歧，
/// 而那时单调的那个是对的。不要把会话层改成读 UTC 来「对齐」。
/// </para>
/// <para>
/// 代价与 <c>JourneyRuntimeEngine</c> 派车前那一处相同：agvId 与会话代只在信封 JSON 里，要扫一遍收件箱。
/// 只有落在存活窗口之内的行才被解析。
/// </para>
/// </remarks>
public static class SessionLiveness
{
    /// <summary>ADR-cross-0027：心跳两秒一次，六秒存活超时。</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    /// <summary>此刻听得到当前这一代会话的车。</summary>
    public static async Task<HashSet<string>> HeardFromAsync(
        ControlServerDbContext context,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        Dictionary<string, long> generations = await context.SessionRecoveries.AsNoTracking()
            .ToDictionaryAsync(
                row => row.AgvId, row => row.SessionGeneration, StringComparer.Ordinal, cancellationToken);
        HashSet<string> heard = new(StringComparer.Ordinal);
        if (generations.Count == 0)
        {
            return heard;
        }

        ProtocolInboxRow[] inbound = await context.ProtocolInbox.AsNoTracking()
            .ToArrayAsync(cancellationToken);
        foreach (ProtocolInboxRow row in inbound)
        {
            if (!InsideWindow(row, now))
            {
                continue;
            }
            if (Identify(row) is not { } identity)
            {
                continue;
            }
            if (generations.TryGetValue(identity.AgvId, out long current) && current == identity.Generation)
            {
                heard.Add(identity.AgvId);
            }
        }
        return heard;
    }

    /// <summary>
    /// 此刻听得到这一台车这一代会话没有。与批量版同一条规则，同样两个谓词——调用方已经知道代次时用它，
    /// 省掉一次 <c>SessionRecoveries</c> 全表读。
    /// </summary>
    /// <remarks>
    /// 加在 control-server#234：旅程运行时要按车判失联，而把那条判定在引擎里再写一遍，正是本类注释里
    /// 「不各自再判一次」禁止的事。
    /// </remarks>
    public static async Task<bool> HeardFromAsync(
        ControlServerDbContext context,
        string agvId,
        long generation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);

        ProtocolInboxRow[] inbound = await context.ProtocolInbox.AsNoTracking()
            .ToArrayAsync(cancellationToken);
        foreach (ProtocolInboxRow row in inbound)
        {
            if (!InsideWindow(row, now))
            {
                continue;
            }
            if (Identify(row) is { } identity &&
                string.Equals(identity.AgvId, agvId, StringComparison.Ordinal) &&
                identity.Generation == generation)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>这一行入站是否落在存活窗口里：收件时间不在未来，且距今不超过 <see cref="Timeout"/>。</summary>
    private static bool InsideWindow(ProtocolInboxRow row, DateTimeOffset now) =>
        row.ReceivedAt <= now && now - row.ReceivedAt <= Timeout;

    /// <summary>
    /// 这一行入站属于哪台车的哪一代会话；信封里两项缺任何一项都不算——握手第一条 <c>SessionHello</c> 还没有
    /// 会话代，它证不了任何一代会话的存活。
    /// </summary>
    private static (string AgvId, long Generation)? Identify(ProtocolInboxRow row)
    {
        using JsonDocument document = JsonDocument.Parse(row.RequestJson);
        JsonElement root = document.RootElement;
        return root.TryGetProperty("agvId", out JsonElement agv) &&
               agv.ValueKind == JsonValueKind.String &&
               agv.GetString() is { } agvId &&
               root.TryGetProperty("sessionGeneration", out JsonElement sessionGeneration) &&
               sessionGeneration.ValueKind == JsonValueKind.Number
            ? (agvId, sessionGeneration.GetInt64())
            : null;
    }
}
