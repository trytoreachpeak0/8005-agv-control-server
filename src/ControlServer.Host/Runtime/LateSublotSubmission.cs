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
/// 而且只看已落库的事实：旅程已收尾；或这一站已完成、已删；或它就是车所在的那一站、一条待做项都没有、阶段已离开等录入。
/// 这一站仍在等录入时——哪怕期限已过——扫码是它的合法答复，交给引擎按「先落库者胜」判（ADR-cross-0055）。
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
                new WireProblem(
                    ServerReasonCodes.WorklistRevisionStale,
                    "payload.worklistRevision",
                    "The stop this sublot was entered for has already ended; its worklist is no longer current."),
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
        if (runtime.Stage == JourneyRuntimeStage.AwaitingSublot)
        {
            return false;
        }
        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken)
            .ConfigureAwait(false);
        return stops.OpenStops.Count > 0 &&
               stops.Current.StopId == stop.StopId &&
               stops.OutstandingAtCurrentStop.Count == 0;
    }
}
