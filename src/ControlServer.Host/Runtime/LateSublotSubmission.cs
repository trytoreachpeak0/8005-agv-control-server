using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 一站已经结束之后才到的扫码：入站那一侧答 <c>SublotRejected</c> / <c>WORKLIST_REVISION_STALE</c>（control-server#324）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么由入站答，而不是引擎。</b>引擎只在 <see cref="JourneyRuntimeStage.AwaitingSublot"/> 里读录入
/// （<c>FindMatchingSublotAsync</c>）。这一站被期限、取消或补偿结束之后旅程离开了那个阶段（或整趟收尾），再到的扫码只得到
/// <c>DurableAck</c>，永远没人回答，车停在「已提交」。
/// </para>
/// <para>
/// <b>只答「这一站确实已经结束」的那些，不抢答。</b>判定在收件箱的写事务里做（<c>BEGIN IMMEDIATE</c>，与取消的授权同一把锁），
/// 而且只看已落库的事实：旅程已收尾；或这一站已完成、已删；或它就是车所在的那一站、一条待做项都没有。
/// 这一站还有待做项时——哪怕期限已过——扫码是它的合法答复，交给引擎按「先落库者胜」判（ADR-cross-0055）。
/// 不看阶段：扫码前取消结束本站后阶段仍是等录入，而那一站已经没有可答的了（PR #361 审查）。
/// </para>
/// <para>
/// <b>答复的 id 与引擎拒收用同一个派生</b>（<see cref="RejectionMessageId"/>）：引擎认「这条录入被答复过」读的正是这一行
/// （<c>LoadCancellationBeforeSublot.RefusedSubmissionIdsAsync</c>），同一站之后若因追加回到等录入，这条过时的扫码不会被再读一遍。
/// </para>
/// </remarks>
internal static class LateSublotSubmission
{
    private const string WorklistType = "CurrentStopWorklistSnapshot";

    /// <summary>对这条录入的拒收报文的 id，与引擎拒收同一个派生。</summary>
    public static string RejectionMessageId(string submissionId) =>
        JourneyPlanBuilder.StableGuid(submissionId, "sublot-rejected");

    /// <summary>
    /// 这条 <c>SublotSubmitted</c> 所答的那一站若已结束，把拒收暂存进本次改动。返回是否暂存了。在收件箱的写事务里调用。
    /// </summary>
    public static async Task<bool> StageRejectionIfStopEndedAsync(
        ControlServerDbContext dbContext,
        JsonElement root,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        string agvId = root.GetProperty("agvId").GetString()!;
        string submissionId = root.GetProperty("messageId").GetString()!;
        JsonElement payload = root.GetProperty("payload");
        string operationSessionId = payload.GetProperty("operationSessionId").GetString()!;
        string stationId = payload.GetProperty("stationId").GetString()!;
        string sublot = payload.GetProperty("sublot").GetString()!;

        // 作业会话不是每个停靠一个：受理时写下的两个停靠共用旅程那一个（单需求旅程今天就是这样）。录入只答取货停靠，
        // 站点再收窄一层；仍不止一个、或一个都没有，就不猜，留给引擎。
        JourneyStopRow[] candidates = await dbContext.Set<JourneyStopRow>().AsNoTracking()
            .Where(row => row.OperationSessionId == operationSessionId &&
                          row.StationId == stationId &&
                          row.StopRole == JourneyStopRoles.Pickup)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Length != 1)
        {
            return false;
        }
        JourneyStopRow stop = candidates[0];
        JourneyRuntimeRow runtime = await dbContext.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == stop.JourneyId, cancellationToken).ConfigureAwait(false);
        if (runtime.AgvId != agvId || !await StopEndedAsync(dbContext, runtime, stop, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // 号告诉车「此刻有效的是哪一版」：这辆车最后收到的那张清单——本站结束时的空清单，或收尾快照。
        long current = await JourneyClosure
            .HighestSentRevisionAsync(dbContext, agvId, WorklistType, cancellationToken).ConfigureAwait(false)
            ?? payload.GetProperty("worklistRevision").GetInt64();
        return await OnboardJourneyPublisher.StageSublotRejectedAsync(
            new WireToGateStore(dbContext),
            RejectionMessageId(submissionId),
            submissionId,
            agvId,
            root.GetProperty("sessionGeneration").GetInt64(),
            new SublotRejection(
                null,
                operationSessionId,
                StaleProblem,
                current,
                sublot),
            now,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>保存之后：这条录入若得到了拒收，发出去。送不到时留在发件箱，同其他答复。</summary>
    public static async Task SendAsync(
        OnboardJourneyPublisher publisher,
        ControlServerDbContext dbContext,
        string submissionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        string messageId = RejectionMessageId(submissionId);
        if (!await dbContext.ProtocolOutbox.AsNoTracking()
                .AnyAsync(row => row.MessageId == messageId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            await publisher.SendPersistedAsync(messageId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // 这一刻车不在线上，同 JourneyClosure.SendAsync。
        }
    }

    private static async Task<bool> StopEndedAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        CancellationToken cancellationToken)
    {
        if (runtime.Stage == JourneyRuntimeStage.Completed ||
            stop.Status is JourneyStopStatuses.Completed or JourneyStopStatuses.Removed)
        {
            return true;
        }
        // 不看阶段：扫码前取消结束本站时，协调器只终结需求、不改阶段，旅程带着 0 个待做项停在 AwaitingSublot，直到离站期限
        // （PR #361 两路审查的必修）。先前这里有一支「阶段是等录入就交给引擎」，于是那段时间里的迟到扫码被引擎按
        // SUBLOT_NOT_IN_DISPATCH_SCOPE、号 r+1 答复。「这一站还在等录入」的真正判据是当前停靠还有待做项，下面这一句就是它。
        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken)
            .ConfigureAwait(false);
        return stops.OpenStops.Count > 0 &&
               stops.Current.StopId == stop.StopId &&
               stops.OutstandingAtCurrentStop.Count == 0;
    }

    /// <summary>
    /// 本站结束的那一次改动里，把答这一站、既没被装货命令消费也没被拒收的录入一并暂存 <c>WORKLIST_REVISION_STALE</c>
    /// （PR #361 审查 S1）。返回暂存了几条。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>窗口在哪。</b>引擎在锁外读收件箱找录入（<c>FindMatchingSublotAsync</c>），期限事务在 <c>BEGIN IMMEDIATE</c> 下只复核阶段与开着的取消。
    /// 扫码若在两者之间落库：入站判「仍在等」只回 <c>DurableAck</c>，随后期限结束本站，这条扫码既不装也不拒，车停在「已提交」。
    /// 扫码前取消赢过一条在途扫码时是同一个样子。
    /// </para>
    /// <para>
    /// 结束本站的那一次改动在同一把写锁里，读到的是锁前所有已提交的录入；锁之后到的，入站判得出「已结束」，走
    /// <see cref="StageRejectionIfStopEndedAsync"/>。两处用同一个派生 id，谁先暂存谁算，另一处认出已有这一行就不再加。
    /// </para>
    /// </remarks>
    public static async Task<int> StageForUnansweredEntriesAsync(
        ControlServerDbContext dbContext,
        StopEntryAddress address,
        long currentWorklistRevision,
        long sessionGeneration,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        List<(string Id, string Sublot)> unanswered = await UnansweredEntriesAsync(dbContext, address, cancellationToken)
            .ConfigureAwait(false);
        int staged = 0;
        WireToGateStore store = new(dbContext);
        foreach ((string submissionId, string sublot) in unanswered)
        {
            if (await OnboardJourneyPublisher.StageSublotRejectedAsync(
                    store,
                    RejectionMessageId(submissionId),
                    submissionId,
                    address.AgvId,
                    sessionGeneration,
                    new SublotRejection(null, address.OperationSessionId, StaleProblem, currentWorklistRevision, sublot),
                    now,
                    cancellationToken).ConfigureAwait(false))
            {
                staged++;
            }
        }
        return staged;
    }

    /// <summary>
    /// 保存之后：这辆车最近一趟旅程车所在（或收尾时所在）的那个取货停靠上，已暂存而车还没确认的过时答复，发出去。
    /// </summary>
    public static async Task SendStaleAnswersAsync(
        OnboardJourneyPublisher publisher,
        ControlServerDbContext dbContext,
        string agvId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        var journeys = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.AgvId == agvId)
            .Select(row => new { row.JourneyId, row.CreatedAt })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var latest = journeys
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.JourneyId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (latest is null)
        {
            return;
        }
        JourneyRuntimeRow runtime = await dbContext.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == latest.JourneyId, cancellationToken).ConfigureAwait(false);
        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
        if (stops.OpenStops.Count == 0 || stops.Current.StopRole != JourneyStopRoles.Pickup)
        {
            return;
        }
        StopEntryAddress address = AddressOf(stops, runtime, stops.Current);
        string[] rejectionIds =
        [
            .. (await AnsweringSubmissionsAsync(dbContext, address, cancellationToken).ConfigureAwait(false))
                .Select(entry => RejectionMessageId(entry.Id))
        ];
        string[] pending = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => rejectionIds.Contains(row.MessageId) && row.AcknowledgedAt == null && row.FencedAt == null)
            .Select(row => row.MessageId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (string messageId in pending)
        {
            try
            {
                await publisher.SendPersistedAsync(messageId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // 这一刻车不在线上，同 JourneyClosure.SendAsync。
            }
        }
    }

    /// <summary>这个停靠的录入地址：作业会话、站点，以及本停靠发过的全部版号（含本站结束时那张空清单占的一版）。</summary>
    internal static StopEntryAddress AddressOf(JourneyStopCursor stops, JourneyRuntimeRow runtime, JourneyStopRow stop)
    {
        long first = stops.FirstWorklistRevisionAt(runtime.WorklistRevision, stop);
        return new StopEntryAddress(
            runtime.AgvId, stop.OperationSessionId, stop.StationId, first, first + stops.WorklistVersionsOf(stop) - 1);
    }

    private static readonly WireProblem StaleProblem = new(
        ServerReasonCodes.WorklistRevisionStale,
        "payload.worklistRevision",
        "The stop this sublot was entered for has already ended; its worklist is no longer current.");

    /// <summary>答这个地址的全部录入（任何一代会话），按收件时刻。</summary>
    private static async Task<List<(string Id, string Sublot)>> AnsweringSubmissionsAsync(
        ControlServerDbContext dbContext,
        StopEntryAddress address,
        CancellationToken cancellationToken)
    {
        string operationSessionId = address.OperationSessionId;
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == "SublotSubmitted" && row.RequestJson.Contains(operationSessionId))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        List<(string Id, string Sublot)> answering = [];
        foreach (ProtocolInboxRow row in rows.OrderBy(row => row.ReceivedAt))
        {
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            if (LoadCancellationBeforeSublot.AnswersTheStop(document.RootElement, address))
            {
                answering.Add((row.MessageId, document.RootElement.GetProperty("payload").GetProperty("sublot").GetString()!));
            }
        }
        return answering;
    }

    /// <summary>答这个地址、既没被装货命令消费也没被拒收的录入。</summary>
    private static async Task<List<(string Id, string Sublot)>> UnansweredEntriesAsync(
        ControlServerDbContext dbContext,
        StopEntryAddress address,
        CancellationToken cancellationToken)
    {
        List<(string Id, string Sublot)> answering = await AnsweringSubmissionsAsync(dbContext, address, cancellationToken)
            .ConfigureAwait(false);
        if (answering.Count == 0)
        {
            return answering;
        }
        HashSet<string> refused = await LoadCancellationBeforeSublot
            .RefusedSubmissionIdsAsync(dbContext, [.. answering.Select(entry => entry.Id)], cancellationToken)
            .ConfigureAwait(false);
        List<(string Id, string Sublot)> unanswered = [];
        foreach ((string id, string sublot) in answering)
        {
            if (refused.Contains(id) || await ConsumedAsync(dbContext, id, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }
            unanswered.Add((id, sublot));
        }
        return unanswered;
    }

    /// <summary>这条录入是否已被一条装货命令答复：发件箱里那条命令的 <c>correlationId</c> 就是它（同引擎的判法）。</summary>
    private static async Task<bool> ConsumedAsync(
        ControlServerDbContext dbContext,
        string submissionId,
        CancellationToken cancellationToken)
    {
        string[] stored = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "SlotOperationCommand" && row.PayloadJson.Contains(submissionId))
            .Select(row => row.PayloadJson)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (string payload in stored)
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("correlationId", out JsonElement correlationId) &&
                string.Equals(correlationId.GetString(), submissionId, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
