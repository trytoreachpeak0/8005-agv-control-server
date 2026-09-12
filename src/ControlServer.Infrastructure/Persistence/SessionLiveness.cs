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
/// 在线时的判定。看板上凡是要说「在线」的地方都经过这里，不各自再判一次——车载告警卡片 2026-09-10 就是因为只看
/// 那一列，断线六十秒后仍显示失联前的告警（<c>docs/defects/20260910-dashboard-kept-showing-a-dead-vehicles-last-alarms.md</c>）。
/// </para>
/// <para>
/// 规则与 <c>JourneyRuntimeEngine</c> 派车前的存活判定一致：取服务端收件时间，不取载荷时间——一个停走或配错的
/// 车载时钟不能让死会话看起来活着；任何入站消息都算，心跳两秒一次；收件时间晚于此刻的不算；只认会话行上那一代，
/// 上一代最后那条心跳再新也不替这一代说话。
/// </para>
/// <para>
/// 代价与引擎那一处相同：agvId 与会话代只在信封 JSON 里，要扫一遍收件箱。只有落在存活窗口之内的行才被解析。
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
            if (row.ReceivedAt > now || now - row.ReceivedAt > Timeout)
            {
                continue;
            }
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("agvId", out JsonElement agv) &&
                agv.ValueKind == JsonValueKind.String &&
                agv.GetString() is { } agvId &&
                generations.TryGetValue(agvId, out long generation) &&
                root.TryGetProperty("sessionGeneration", out JsonElement sessionGeneration) &&
                sessionGeneration.ValueKind == JsonValueKind.Number &&
                sessionGeneration.GetInt64() == generation)
            {
                heard.Add(agvId);
            }
        }
        return heard;
    }
}
