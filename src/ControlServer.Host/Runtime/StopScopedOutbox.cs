using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 以站为生命周期的出站行：<c>SublotEntryRequested</c> 与 <c>SublotRejected</c>。站结束、旅程终态之后把它们退役（control-server#740）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要退。</b>站结束之后车载端不收这一站的录入请求与拒收，也不回 ack（车载端日志「录入请求所属的站已结束，不收下」）。
/// 服务端此前只退上一号或当前号（<see cref="StopEndWorklist"/>、引擎的 <c>PublishEntryRequestAsync</c>），更早的版与拒收留在发件箱里
/// 未确认、未退役：每次重连补发、车每次拒收；换协议身份时 <c>ProtocolOutboxIdentityStartupCheck</c> 拒绝启动。10-17 v4 换装因此只能换新库。
/// </para>
/// <para>
/// <b>按停靠认，不按修订号算。</b>两类载荷都带 <c>operationSessionId</c>，它就是停靠行的 <see cref="JourneyStopRow.OperationSessionId"/>。
/// 按号派生 id 去找，漏掉的正是派生没覆盖到的那几版；按会话找，不管是哪条来路写下的、哪一版，都在里面。
/// </para>
/// <para>
/// <b>只退已落库的行。</b>查的是库，本次改动里刚暂存（<c>Added</c>）的不在结果里：收尾与站结束时答迟到扫码新暂存的那一条
/// <c>SublotRejected</c>（<see cref="LateSublotSubmission"/>）是车要收的，不能与旧行一起退。它若之后一直没被确认，下一次退役
/// （这辆车下一次离站或收尾，或启动修复）会把它当遗留行退掉。
/// </para>
/// <para>
/// <b>停靠收尾状态不在这里。</b>判「已结束」读的是停靠的 <c>Status</c>（<c>Completed</c>／<c>Removed</c>）与旅程的 <c>Stage</c>（<c>Completed</c>）；
/// 需求已终结而停靠仍 <c>PENDING</c> 的，靠旅程终态这一条认出来，不在这里改停靠状态。
/// </para>
/// </remarks>
internal static class StopScopedOutbox
{
    private static readonly string[] MessageTypes = ["SublotEntryRequested", "SublotRejected"];

    private static readonly Action<ILogger, int, Exception?> LogFencedAtStartup =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(9412, "StopScopedOutboxFencedAtStartup"),
            "Fenced {Count} SublotEntryRequested/SublotRejected line(s) of ended stops left unacknowledged (control-server#740).");

    /// <summary>
    /// 退役这一个停靠已落库、未确认、未退役的录入请求与拒收。只暂存，不保存。
    /// </summary>
    public static async Task FenceStopAsync(
        ControlServerDbContext dbContext,
        string operationSessionId,
        DateTimeOffset fencedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationSessionId);

        foreach ((ProtocolOutboxRow row, _, string session) in await PendingAsync(dbContext, cancellationToken).ConfigureAwait(false))
        {
            if (session == operationSessionId)
            {
                row.FencedAt = fencedAt;
            }
        }
    }

    /// <summary>
    /// 退役所属停靠已结束（<c>Completed</c>／<c>Removed</c>）或所属旅程已终态的录入请求与拒收。<paramref name="agvId"/> 为 null 时不分车。
    /// 只暂存，不保存；返回退役了几行。
    /// </summary>
    /// <remarks>
    /// 停靠与旅程用被跟踪的查询读：调用方同一次改动里刚把停靠标成 <c>Completed</c>、把旅程标成终态的，这里读到的是那个值。
    /// 载荷里的会话对不上任何停靠的行不动——认不出它属于谁，就不替它判。
    /// </remarks>
    public static async Task<int> FenceEndedAsync(
        ControlServerDbContext dbContext,
        string? agvId,
        DateTimeOffset fencedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var pending = (await PendingAsync(dbContext, cancellationToken).ConfigureAwait(false))
            .Where(line => agvId is null || line.AgvId == agvId)
            .ToArray();
        if (pending.Length == 0)
        {
            return 0;
        }

        string[] sessions = [.. pending.Select(line => line.Session).Distinct(StringComparer.Ordinal)];
        List<JourneyStopRow> stops = await dbContext.Set<JourneyStopRow>()
            .Where(row => sessions.Contains(row.OperationSessionId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        string[] journeyIds = [.. stops.Select(row => row.JourneyId).Distinct(StringComparer.Ordinal)];
        HashSet<string> endedJourneys = new(
            (await dbContext.JourneyRuntimes
                .Where(row => journeyIds.Contains(row.JourneyId))
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(row => row.Stage == JourneyRuntimeStage.Completed)
            .Select(row => row.JourneyId),
            StringComparer.Ordinal);
        HashSet<string> endedSessions = new(
            stops
                .Where(row => row.Status is JourneyStopStatuses.Completed or JourneyStopStatuses.Removed ||
                              endedJourneys.Contains(row.JourneyId))
                .Select(row => row.OperationSessionId),
            StringComparer.Ordinal);

        int fenced = 0;
        foreach ((ProtocolOutboxRow row, _, string session) in pending)
        {
            if (endedSessions.Contains(session))
            {
                row.FencedAt = fencedAt;
                fenced++;
            }
        }
        return fenced;
    }

    /// <summary>
    /// 启动修复：修之前写下的遗留行（所属停靠已结束或旅程已终态）在 <c>ProtocolOutboxIdentityStartupCheck</c> 之前退役并保存。幂等，每次启动都跑。
    /// </summary>
    /// <remarks>启动检查本身不放宽：还开着的停靠上、身份不对的行照旧挡住启动。</remarks>
    public static async Task FenceEndedAtStartupAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IServiceProvider provider = scope.ServiceProvider;
        ControlServerDbContext dbContext = provider.GetRequiredService<ControlServerDbContext>();
        int fenced = await FenceEndedAsync(
                dbContext, agvId: null, (provider.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        if (fenced == 0)
        {
            return;
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogFencedAtStartup(
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(StopScopedOutbox).FullName!), fenced, null);
    }

    /// <summary>已落库、未确认、未退役的这两类行，连同信封里的车与载荷里的作业会话。读不出会话的行不在结果里。</summary>
    private static async Task<List<(ProtocolOutboxRow Row, string? AgvId, string Session)>> PendingAsync(
        ControlServerDbContext dbContext,
        CancellationToken cancellationToken)
    {
        ProtocolOutboxRow[] rows = await dbContext.ProtocolOutbox
            .Where(row => MessageTypes.Contains(row.MessageType) && row.AcknowledgedAt == null && row.FencedAt == null)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        List<(ProtocolOutboxRow, string?, string)> lines = [];
        // 被跟踪的实体按本次改动里的值判：调用方刚确认（StopEndWorklist 结清当前那一版）的不再退。
        foreach (ProtocolOutboxRow row in rows.Where(row => row.AcknowledgedAt is null && row.FencedAt is null))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("payload", out JsonElement payload) &&
                    payload.ValueKind == JsonValueKind.Object &&
                    payload.TryGetProperty("operationSessionId", out JsonElement session) &&
                    session.ValueKind == JsonValueKind.String &&
                    session.GetString() is { Length: > 0 } operationSessionId)
                {
                    string? agvId = root.TryGetProperty("agvId", out JsonElement agv) && agv.ValueKind == JsonValueKind.String
                        ? agv.GetString()
                        : null;
                    lines.Add((row, agvId, operationSessionId));
                }
            }
            catch (JsonException)
            {
                // 读不出来的行不替它判；身份检查照旧把它算作不匹配。
            }
        }
        return lines;
    }
}
