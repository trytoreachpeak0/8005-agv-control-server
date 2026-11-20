using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 本站一条需求被扫码前取消、本站还有别的待做项时，与终结同一次改动暂存去掉它的新一版清单（等录入时连同录入请求），保存后立即发出
/// （control-server#909）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要在取消这一侧发。</b>本站因取消而结束时，<see cref="StopEndWorklist"/> 随终结那一次保存暂存空清单，0.2 秒就到车上。本站还有别的
/// 待做项时，修之前谁也不在这一侧发：号随终结前进了（<see cref="JourneyStopCursor.WorklistRevisionAt"/>），新的一版却要等引擎下一轮由
/// <c>JourneyRuntimeEngine.WorklistBehindAnEndingAtTheStopAsync</c>（control-server#723）认出来再发。而那一轮的轮首补发先把车上那一版
/// ——仍期待被取消的子批、还没人答的录入请求——原样再发一遍，车重新拿到它，屏上被取消的那一行约 10 秒后又能按。生产 11-17～11-20
/// agv02 这样的取消 91 次，新清单中位 11.7 秒、最长 41.6 秒才到，91 次全部夹着一遍同版重发。
/// </para>
/// <para>
/// <b>发的就是引擎会发的那一版。</b>号、消息 id 都按游标的算式（<see cref="JourneyStopCursor.WorklistRevisionAt"/>、
/// <see cref="JourneyStopCursor.WorklistMessageIdAt"/>），项由 <see cref="JourneyRuntimeEngine.Worklist"/> 投影，较早各版（清单与录入请求）
/// 全部退役——与 #723 那一段逐项相同。于是引擎下一轮看到此刻那一版已在发件箱里，#723 那一段不再动作；号与 id 的算法一字未改
/// （cs#879 教训）。#723 那一段留着：这里没做成的（没有会话、本站还没发过任何一版），下一轮照旧由它补。
/// </para>
/// <para>
/// <b>期限沿用车上那一版。</b>取消改的是本站的需求集合，不改离站等待；新的一版带车手上最新那一版的 <c>stationDepartureDeadlineAt</c>。
/// 车上那一版若已过时（断联作废了离站等待），引擎的 <c>AdvanceWorklistPastAStaleDeadlineAsync</c> 照旧按「排给车的那一版与服务端此刻的期限是否一致」
/// 升一版——判据读的正是这里暂存的这一行，不靠这里自己算期限。
/// </para>
/// <para>
/// <b>录入请求只在等录入时发。</b>本站另一条正在装时没有开着的录入，与引擎在装那一格一样只发清单；下一条的录入请求在这一批落定之后随下一版发出。
/// </para>
/// <para>
/// <b>与终结同一次改动。</b>只暂存，不保存：新的一版、旧各版的退役随调用方那一次保存落库，崩在中间不会留下「号前进了、旧版还在补发」。保存之后由
/// 调用方 <see cref="SendAsync"/>；送不到时留在发件箱，由引擎每轮顶上的补发送出（id 就是补发白名单里此刻那一版的 id）。
/// </para>
/// </remarks>
internal static class RemainingStopWorklist
{
    private const string WorklistType = "CurrentStopWorklistSnapshot";

    /// <summary>
    /// 终结了 <paramref name="demandId"/> 之后，若它挂在车此刻所在的取货停靠上、而这一站还有待做项，暂存新的一版。返回是否暂存了。
    /// </summary>
    public static async Task<bool> StageAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        string demandId,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);

        JourneyStopCursor staged = await JourneyStopCursor
            .LoadIncludingUnsavedChangesAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
        if (staged.OpenStops.Count == 0)
        {
            return false;
        }
        JourneyStopRow stop = staged.Current;
        IReadOnlyList<JourneyStopDemand> outstanding = staged.OutstandingAtCurrentStop;
        // 本站因此结束的那一种归 StopEndWorklist；终结的是后面某一站的需求时，车所在这一站的清单不变。
        if (stop.StopRole != JourneyStopRoles.Pickup ||
            outstanding.Count == 0 ||
            staged.AllAtStop(stop).All(item => item.Demand.DemandId != demandId))
        {
            return false;
        }
        SessionRecoveryRow? session = await dbContext.SessionRecoveries.AsNoTracking()
            .SingleOrDefaultAsync(row => row.AgvId == runtime.AgvId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return false;
        }

        long first = staged.FirstWorklistRevisionAt(runtime.WorklistRevision, stop);
        long revision = staged.WorklistRevisionAt(runtime.WorklistRevision, stop);
        string worklistId = staged.WorklistMessageIdAt(runtime.WorklistRevision, stop, revision);
        if (await QueuedAsync(dbContext, worklistId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        // 车手上最新那一版：本站一版都还没发过（到站那一段还没跑完）就不在这里发，到站那一段发的就是此刻的集合。
        if (await LatestSentAtStopAsync(dbContext, staged, runtime, stop, first, revision, cancellationToken)
                .ConfigureAwait(false) is not { } latest)
        {
            return false;
        }

        // 本站较早各版的清单与录入请求都退役：车上那一版不一定是上一号（两次终结落在同一轮之间时号跳一号），留着未确认的会被补发、
        // 排在新的一版之后到车上，车判修订号回退；留着的录入请求就是那遍同版重发（control-server#723、#909）。
        for (long earlier = first; earlier < revision; earlier++)
        {
            await StopEndWorklist.FenceAsync(
                dbContext, staged.WorklistMessageIdAt(runtime.WorklistRevision, stop, earlier), endedAt, cancellationToken)
                .ConfigureAwait(false);
            await StopEndWorklist.FenceAsync(
                dbContext, staged.SublotRequestMessageIdAt(runtime.WorklistRevision, stop, earlier), endedAt, cancellationToken)
                .ConfigureAwait(false);
        }

        WireToGateStore store = new(dbContext);
        await OnboardJourneyPublisher.StageCurrentStopWorklistAsync(
            store,
            worklistId,
            runtime.AgvId,
            session.SessionGeneration,
            JourneyRuntimeEngine.Worklist(stop, outstanding, revision, latest.Deadline),
            endedAt,
            cancellationToken).ConfigureAwait(false);
        if (runtime.Stage == JourneyRuntimeStage.AwaitingSublot)
        {
            await OnboardJourneyPublisher.StageSublotEntryRequestAsync(
                store,
                staged.SublotRequestMessageIdAt(runtime.WorklistRevision, stop, revision),
                runtime.AgvId,
                session.SessionGeneration,
                new SublotEntryRequest(
                    stop.OperationSessionId,
                    stop.StationId,
                    revision,
                    [.. outstanding.Select(item => item.Demand.Sublot)]),
                endedAt,
                cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// 保存之后：这辆车进行中的旅程里，当前停靠此刻那一版清单还没被车确认（也没退役）时，把它和同号的录入请求发出去。都已确认或没有就什么也不做。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>按状态认，不记「这一次暂存了什么」。</b>与 <see cref="StopEndWorklist.SendAsync"/>、<see cref="JourneyClosure.SendAsync"/> 同一个做法：
    /// 每条恢复结果之后都问一次。车确认了此刻那一版清单就不再发，所以之后的恢复结果不会把录入请求再发一遍；车没确认（没连着、刚重连）时多发一遍
    /// 同号同内容，车按 id 去重——引擎轮首的补发本来就这样送它。
    /// </para>
    /// <para>送不到（没连着、握手中、代次不符、连接正在拆）不算失败，同 <see cref="JourneyClosure.SendAsync"/> 的理由。</para>
    /// </remarks>
    public static async Task SendAsync(
        OnboardJourneyPublisher publisher,
        ControlServerDbContext dbContext,
        string agvId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(dbContext);
        var journeys = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.AgvId == agvId)
            .Select(row => new { row.JourneyId, row.Stage, row.CreatedAt })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var latest = journeys
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.JourneyId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (latest is null || latest.Stage == JourneyRuntimeStage.Completed)
        {
            return;
        }
        JourneyRuntimeRow runtime = await dbContext.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == latest.JourneyId, cancellationToken).ConfigureAwait(false);
        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken)
            .ConfigureAwait(false);
        if (stops.OpenStops.Count == 0 ||
            stops.Current.StopRole != JourneyStopRoles.Pickup ||
            stops.OutstandingAtCurrentStop.Count == 0)
        {
            return;
        }
        long revision = stops.WorklistRevisionAt(runtime.WorklistRevision, stops.Current);
        // 首版是到站那一段发的，不归这里。
        if (revision == stops.FirstWorklistRevisionAt(runtime.WorklistRevision, stops.Current))
        {
            return;
        }
        string worklistId = stops.WorklistMessageIdAt(runtime.WorklistRevision, stops.Current, revision);
        bool pending = await dbContext.ProtocolOutbox.AsNoTracking()
            .AnyAsync(
                row => row.MessageId == worklistId && row.AcknowledgedAt == null && row.FencedAt == null,
                cancellationToken)
            .ConfigureAwait(false);
        if (!pending)
        {
            return;
        }
        try
        {
            await publisher.SendPersistedAsync(worklistId, cancellationToken).ConfigureAwait(false);
            string entryId = stops.SublotRequestMessageIdAt(runtime.WorklistRevision, stops.Current, revision);
            if (await dbContext.ProtocolOutbox.AsNoTracking()
                    .AnyAsync(row => row.MessageId == entryId, cancellationToken).ConfigureAwait(false))
            {
                await publisher.SendPersistedAsync(entryId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // 留在发件箱里：引擎每轮顶上的补发按白名单送它。
        }
    }

    private static async Task<bool> QueuedAsync(
        ControlServerDbContext dbContext, string messageId, CancellationToken cancellationToken) =>
        dbContext.ProtocolOutbox.Local.Any(row => row.MessageId == messageId) ||
        await dbContext.ProtocolOutbox.AsNoTracking()
            .AnyAsync(row => row.MessageId == messageId, cancellationToken).ConfigureAwait(false);

    private sealed record SentWorklist(long Revision, DateTimeOffset? Deadline);

    /// <summary>本停靠此刻那一版之前、已经排进发件箱的最高一版及其期限；一版都没有为空。</summary>
    private static async Task<SentWorklist?> LatestSentAtStopAsync(
        ControlServerDbContext dbContext,
        JourneyStopCursor staged,
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        long first,
        long revision,
        CancellationToken cancellationToken)
    {
        for (long earlier = revision - 1; earlier >= first; earlier--)
        {
            string id = staged.WorklistMessageIdAt(runtime.WorklistRevision, stop, earlier);
            string? payloadJson = await dbContext.ProtocolOutbox.AsNoTracking()
                .Where(row => row.MessageId == id && row.MessageType == WorklistType)
                .Select(row => row.PayloadJson)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (payloadJson is null)
            {
                continue;
            }
            using JsonDocument document = JsonDocument.Parse(payloadJson);
            JsonElement payload = document.RootElement.GetProperty("payload");
            DateTimeOffset? deadline = payload.TryGetProperty("stationDepartureDeadlineAt", out JsonElement at) &&
                                       at.ValueKind != JsonValueKind.Null
                ? at.GetDateTimeOffset()
                : null;
            return new SentWorklist(earlier, deadline);
        }
        return null;
    }
}
