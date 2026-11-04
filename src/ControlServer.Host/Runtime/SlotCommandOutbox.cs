using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 已有结论的 <c>SlotOperationCommand</c> 退出补发：车已作答的记为已确认，服务端已另行了结的退役（control-server#623）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要做。</b>这条命令的应答是 <c>OperationResult</c>，不是 DurableAck，所以要由服务端显式结算。修之前只在两处结算：装货 Committed
/// 与确定的装货失败。结果回 RecoveryRequired 之后，补偿、交接、强制取出终结那一条，cs#499 把旅程放回，走的是 <c>resumingAfterCommit</c>，
/// 跳过结算——原命令每轮补发一次，车载端每次当重复命令忽略、什么也不回，行永不确认（agv01 10-22 attempt 3f01992d，约每 5.2 秒一次）。
/// 换协议身份时 <c>ProtocolOutboxIdentityStartupCheck</c> 见到它就拒绝启动。正常 Committed 那一路也有同形的小毛病：结果在恢复期间到，
/// 下一轮就绪时先补发、后结算，多发一次（cs#205 真装置证据）。
/// </para>
/// <para>
/// <b>按事实判，不按来路。</b>一行未确认、未退役的命令，按它载荷里的 <c>slotOperationAttemptId</c> 与它所属的归属判：
/// </para>
/// <list type="number">
/// <item>这个 attempt 已有 <c>OperationResult</c>（任何一份）：车已作答，记为已确认。车载端按 attempt 去重，再发它也只会忽略。</item>
/// <item>没有结果，但站点操作已不是 <c>Prepared</c>（取消、补偿、扣作恢复）：服务端已另行了结，退役。再发等于叫车去做一件已被撤掉的事。</item>
/// <item>站点操作仍是 <c>Prepared</c>，而所属需求已终结、已移出旅程，或所属旅程已收尾：退役，理由同上。</item>
/// </list>
/// <para>
/// 其余（站点操作 <c>Prepared</c>、需求与旅程都还活着，即仍在等结果）照常补发。认不出 attempt 或归属的行不动——认不出它属于谁，就不替它判。
/// 修复续行（<c>RESUME_AFTER_REPAIR</c>）发的是另一条 <c>SlotOperationResumeCommand</c>，不靠补发这一行，不受影响。
/// </para>
/// <para>
/// <b>三处调用。</b>引擎每轮补发之前（只这辆车）；<see cref="JourneyClosure"/> 收尾时；启动时在身份检查之前（修之前留下的行）。
/// 站点操作、归属与旅程都用被跟踪的查询读：调用方同一次改动里刚改的状态，这里读到的是那个值。
/// </para>
/// </remarks>
public static class SlotCommandOutbox
{
    private const string MessageType = "SlotOperationCommand";

    private static readonly Action<ILogger, int, int, Exception?> LogConcludedAtStartup =
        LoggerMessage.Define<int, int>(
            LogLevel.Information,
            new EventId(9413, "SlotCommandOutboxConcludedAtStartup"),
            "Settled {Answered} answered and fenced {Withdrawn} withdrawn SlotOperationCommand line(s) left unacknowledged (control-server#623).");

    /// <summary>一行命令的结论。</summary>
    public enum Conclusion
    {
        /// <summary>仍在等结果，照常补发。</summary>
        Open,

        /// <summary>车已作答：记为已确认。</summary>
        Answered,

        /// <summary>服务端已另行了结：退役。</summary>
        Withdrawn
    }

    /// <summary>
    /// 结算或退役已有结论的命令行。<paramref name="agvId"/> 为 null 时不分车。只暂存，不保存；返回（已确认几行，退役几行）。
    /// </summary>
    public static async Task<(int Answered, int Withdrawn)> ConcludeAsync(
        ControlServerDbContext dbContext,
        string? agvId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        int answered = 0;
        int withdrawn = 0;
        foreach ((ProtocolOutboxRow row, Conclusion conclusion) in await JudgeAsync(dbContext, agvId, cancellationToken)
                     .ConfigureAwait(false))
        {
            switch (conclusion)
            {
                case Conclusion.Answered:
                    row.AcknowledgedAt = at;
                    answered++;
                    break;
                case Conclusion.Withdrawn:
                    row.FencedAt = at;
                    withdrawn++;
                    break;
            }
        }
        return (answered, withdrawn);
    }

    /// <summary>
    /// 每一行未确认、未退役的命令及其结论（只读，不改任何行）。<paramref name="agvId"/> 为 null 时不分车。
    /// 只读的残留检查（<see cref="OutboxResidue"/>，FieldOps <c>outbox-residue</c>）与 <see cref="ConcludeAsync"/> 共用这一处判定。
    /// </summary>
    public static async Task<IReadOnlyList<(ProtocolOutboxRow Row, Conclusion Conclusion)>> JudgeAsync(
        ControlServerDbContext dbContext,
        string? agvId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        ProtocolOutboxRow[] rows = await dbContext.ProtocolOutbox
            .Where(row => row.MessageType == MessageType && row.AcknowledgedAt == null && row.FencedAt == null)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        // 被跟踪的实体按本次改动里的值判：调用方刚结算的不再算。
        List<(ProtocolOutboxRow Row, string Attempt)> pending = [];
        foreach (ProtocolOutboxRow row in rows.Where(row => row.AcknowledgedAt is null && row.FencedAt is null))
        {
            if (Read(row) is { } line && (agvId is null || line.AgvId == agvId))
            {
                pending.Add((row, line.Attempt));
            }
        }
        if (pending.Count == 0)
        {
            return [];
        }

        string[] attempts = [.. pending.Select(line => line.Attempt).Distinct(StringComparer.Ordinal)];
        string[] messageIds = [.. pending.Select(line => line.Row.MessageId)];
        HashSet<string> withResult = new(
            await dbContext.OperationResults.AsNoTracking()
                .Where(row => attempts.Contains(row.SlotOperationAttemptId))
                .Select(row => row.SlotOperationAttemptId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false),
            StringComparer.Ordinal);
        Dictionary<string, StationOperationStatus> operations = (await dbContext.StationOperations
                .Where(row => attempts.Contains(row.SlotOperationAttemptId))
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(row => row.SlotOperationAttemptId, row => row.Status, StringComparer.Ordinal);
        List<JourneyDemandRow> memberships = await dbContext.Set<JourneyDemandRow>()
            .Where(row => messageIds.Contains(row.LoadCommandMessageId) || messageIds.Contains(row.UnloadCommandMessageId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        string[] journeyIds = [.. memberships.Select(row => row.JourneyId).Distinct(StringComparer.Ordinal)];
        HashSet<string> endedJourneys = new(
            (await dbContext.JourneyRuntimes
                .Where(row => journeyIds.Contains(row.JourneyId))
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(row => row.Stage == JourneyRuntimeStage.Completed)
            .Select(row => row.JourneyId),
            StringComparer.Ordinal);

        List<(ProtocolOutboxRow, Conclusion)> judged = [];
        foreach ((ProtocolOutboxRow row, string attempt) in pending)
        {
            Conclusion conclusion;
            if (withResult.Contains(attempt))
            {
                conclusion = Conclusion.Answered;
            }
            else if (operations.TryGetValue(attempt, out StationOperationStatus status) && status != StationOperationStatus.Prepared)
            {
                conclusion = Conclusion.Withdrawn;
            }
            else
            {
                // 同一个命令 id 只属于一条归属；改派换新代次时 id 也换了，所以按 id 认出的归属就是发它的那一条。
                JourneyDemandRow? membership = memberships.FirstOrDefault(item =>
                    item.LoadCommandMessageId == row.MessageId || item.UnloadCommandMessageId == row.MessageId);
                bool ended = membership is not null &&
                             (membership.Status == JourneyDemandStatuses.Terminated ||
                              membership.RemovedAt is not null ||
                              endedJourneys.Contains(membership.JourneyId));
                conclusion = ended ? Conclusion.Withdrawn : Conclusion.Open;
            }
            judged.Add((row, conclusion));
        }
        return judged;
    }

    /// <summary>
    /// 启动修复：修之前留下的、已有结论的命令行在 <c>ProtocolOutboxIdentityStartupCheck</c> 之前结算或退役并保存。幂等，每次启动都跑。
    /// </summary>
    /// <remarks>启动检查本身不放宽：仍在等结果、身份不对的命令照旧挡住启动。</remarks>
    public static async Task ConcludeAtStartupAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IServiceProvider provider = scope.ServiceProvider;
        ControlServerDbContext dbContext = provider.GetRequiredService<ControlServerDbContext>();
        (int answered, int withdrawn) = await ConcludeAsync(
                dbContext, agvId: null, (provider.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        if (answered + withdrawn == 0)
        {
            return;
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogConcludedAtStartup(
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SlotCommandOutbox).FullName!), answered, withdrawn, null);
    }

    /// <summary>信封里的车与载荷里的 attempt；读不出来的行返回 null（身份检查照旧把它算作不匹配）。</summary>
    private static (string? AgvId, string Attempt)? Read(ProtocolOutboxRow row)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("payload", out JsonElement payload) &&
                payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("slotOperationAttemptId", out JsonElement attempt) &&
                attempt.ValueKind == JsonValueKind.String &&
                attempt.GetString() is { Length: > 0 } attemptId)
            {
                string? agvId = root.TryGetProperty("agvId", out JsonElement agv) && agv.ValueKind == JsonValueKind.String
                    ? agv.GetString()
                    : null;
                return (agvId, attemptId);
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }
}
