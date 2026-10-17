using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 已被更高一版盖过的旅程快照（业务状态、清单、计划）：未确认、未退役、车反正会当回退拒收的行退役（control-server#750）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要退。</b>车载端按消息类型记它采纳过的最高号（onboard-hmi <c>WireToGateSessionClient.ApplyJourneyRevision</c>），号更低的
/// 抛 <c>SNAPSHOT_REVISION_REGRESSION</c>、不回 ack；这份记录断线不丢，重连前从本地日志簿恢复。服务端只退同一停靠的上一号与少数几种站结束，
/// 每个停靠的最后一版与已收尾旅程的中途版都只靠车确认。断线漏了确认的那一行之后既不在任何补发集合里、也不会被退，
/// 换协议身份时 <c>ProtocolOutboxIdentityStartupCheck</c> 拒绝启动。
/// </para>
/// <para>
/// <b>流按「车 + 消息类型」划分，不按停靠。</b>车载端那张表以消息类型为键，每个客户端只对一辆车；服务端的号也是按车按类型单调
/// （<c>VehicleSnapshotRevisions</c>、<c>WireToGateStore.RaiseSnapshotRevisionFloorAsync</c>），跨旅程只增不减。
/// <c>ExceptionRecoverySessionSnapshot</c> 不走那张表，不在这里。
/// </para>
/// <para>
/// <b>两条规则，严格小于才退。</b>
/// A：同一流里有车确认过的行时，号低于确认过的最大号的未确认行退役——车已经采纳了更高的，补发只会被拒。
/// B：已收尾旅程的收尾快照（<see cref="JourneyClosure.SnapshotMessageIds"/>，确认与否不论）已落库时，同一流里号低于它、且比它先写下的未确认行退役——
/// 收尾快照的号在本流之上（计划与业务状态取「最大号 + 1」，计数器同时抬高），旅程收尾之后中途版不再在任何补发集合里，
/// 而下一趟旅程的号与写入时刻都在收尾快照之后，两个条件都碰不到它。收尾快照自身（号相等）不退，照旧由 <c>ReplayIdsAsync</c> 补发。
/// </para>
/// <para>
/// <b>运行时只做 A。</b>收尾时刚暂存的收尾快照还没落库、查不到，B 在这里没有依据；收尾时的中途版留到下一次启动修复由 B 退。
/// 它们不在补发集合里，留到那时不会被发出去。
/// </para>
/// </remarks>
internal static class SupersededSnapshotOutbox
{
    private static readonly Dictionary<string, string> RevisionProperties = new(StringComparer.Ordinal)
    {
        ["VehicleBusinessStateSnapshot"] = "vehicleBusinessStateRevision",
        ["CurrentStopWorklistSnapshot"] = "worklistRevision",
        ["UpcomingStopPlanSnapshot"] = "planRevision"
    };

    private static readonly Action<ILogger, int, Exception?> LogFencedAtStartup =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(9413, "SupersededSnapshotOutboxFencedAtStartup"),
            "Fenced {Count} superseded journey snapshot line(s) left unacknowledged (control-server#750).");

    /// <summary>
    /// 规则 A：这辆车（<paramref name="agvId"/> 为 null 时全车队）每条快照流里，号低于车确认过的最大号的未确认、未退役行退役。只暂存，不保存；返回退役了几行。
    /// </summary>
    public static Task<int> FenceAcknowledgedOverAsync(
        ControlServerDbContext dbContext,
        string? agvId,
        DateTimeOffset fencedAt,
        CancellationToken cancellationToken) =>
        FenceAsync(dbContext, agvId, fencedAt, closures: false, cancellationToken);

    /// <summary>规则 A 与 B（见类注释）。只暂存，不保存；返回退役了几行。</summary>
    public static Task<int> FenceSupersededAsync(
        ControlServerDbContext dbContext,
        string? agvId,
        DateTimeOffset fencedAt,
        CancellationToken cancellationToken) =>
        FenceAsync(dbContext, agvId, fencedAt, closures: true, cancellationToken);

    /// <summary>
    /// 启动修复：规则 A 与 B 退役并保存，在 <c>ProtocolOutboxIdentityStartupCheck</c> 之前。幂等，每次启动都跑。
    /// </summary>
    /// <remarks>启动检查本身不放宽：没被盖过的行（在途旅程未确认的最新版）身份不对照旧挡住启动。</remarks>
    public static async Task FenceSupersededAtStartupAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IServiceProvider provider = scope.ServiceProvider;
        ControlServerDbContext dbContext = provider.GetRequiredService<ControlServerDbContext>();
        int fenced = await FenceSupersededAsync(
                dbContext, agvId: null, (provider.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        if (fenced == 0)
        {
            return;
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogFencedAtStartup(
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SupersededSnapshotOutbox).FullName!), fenced, null);
    }

    private static async Task<int> FenceAsync(
        ControlServerDbContext dbContext,
        string? agvId,
        DateTimeOffset fencedAt,
        bool closures,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        // 被跟踪的查询：调用方同一次改动里刚确认、刚退役的行按改动后的值判。只读已落库的行，本次刚暂存的（收尾快照）不在里面。
        string[] types = [.. RevisionProperties.Keys];
        ProtocolOutboxRow[] rows = await dbContext.ProtocolOutbox
            .Where(row => types.Contains(row.MessageType))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        List<Line> lines = [];
        foreach (ProtocolOutboxRow row in rows)
        {
            if (Read(row) is { } line && (agvId is null || line.AgvId == agvId))
            {
                lines.Add(line);
            }
        }

        HashSet<string> closureIds = closures
            ? await ClosureIdsAsync(dbContext, agvId, cancellationToken).ConfigureAwait(false)
            : [];

        int fenced = 0;
        foreach (IGrouping<(string AgvId, string MessageType), Line> stream in lines.GroupBy(line => (line.AgvId, line.Row.MessageType)))
        {
            long? acknowledged = stream.Where(line => line.Row.AcknowledgedAt is not null).Max(line => (long?)line.Revision);
            Line[] closing = [.. stream.Where(line => closureIds.Contains(line.Row.MessageId))];
            foreach (Line line in stream.Where(line => line.Row.AcknowledgedAt is null && line.Row.FencedAt is null))
            {
                bool overAcknowledged = line.Revision < acknowledged;
                bool overClosure = closing.Any(close => line.Revision < close.Revision && line.Row.CreatedAt <= close.Row.CreatedAt);
                if (overAcknowledged || overClosure)
                {
                    line.Row.FencedAt = fencedAt;
                    fenced++;
                }
            }
        }
        return fenced;
    }

    /// <summary>已收尾旅程的收尾快照 id。</summary>
    private static async Task<HashSet<string>> ClosureIdsAsync(
        ControlServerDbContext dbContext,
        string? agvId,
        CancellationToken cancellationToken)
    {
        string[] journeyIds = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.Stage == JourneyRuntimeStage.Completed && (agvId == null || row.AgvId == agvId))
            .Select(row => row.JourneyId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return new HashSet<string>(journeyIds.SelectMany(JourneyClosure.SnapshotMessageIds), StringComparer.Ordinal);
    }

    /// <summary>信封里的车与载荷里的号。读不出来的行返回 null，不替它判；身份检查照旧把它算作不匹配。</summary>
    private static Line? Read(ProtocolOutboxRow row)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("agvId", out JsonElement agv) &&
                agv.ValueKind == JsonValueKind.String &&
                agv.GetString() is { Length: > 0 } agvId &&
                root.TryGetProperty("payload", out JsonElement payload) &&
                payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty(RevisionProperties[row.MessageType], out JsonElement revision) &&
                revision.ValueKind == JsonValueKind.Number &&
                revision.TryGetInt64(out long value))
            {
                return new Line(row, agvId, value);
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private sealed record Line(ProtocolOutboxRow Row, string AgvId, long Revision);
}
