using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 一站结束、旅程继续时（program#86 v2 的 B 形态）给车的那张空清单（control-server#324）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要发。</b>一趟多需求旅程在第二个取货站被期限（或取消、补偿）结束了这一站最后一条待做项，车上却还带着更早装上的货：
/// 旅程不收尾，收尾快照（<see cref="JourneyClosure"/>）一张都不发，而车可能持货等单（REQ-0354）在这一站停很久。这段时间车上仍列着
/// 被终结的那条、亮着录入入口与「取消装货」按钮。车只认更高修订号的清单，所以要发一张：<c>items</c> 为空、作业会话与离站期限为 null。
/// </para>
/// <para>
/// <b>号数。</b>一站 N 条需求发 N 版清单，按 <see cref="JourneyStopCursor.WorklistRevisionAt"/> 做完最后一条恰好等于<b>下一站的首号</b>
/// ——那个号本来就不发（「做完最后一条车直接离站」）。空清单用这个号，同时让本停靠多占一版（<see cref="JourneyStopRow.WorklistRefills"/>
/// 加一），后面每一站的首号跟着顺延一号。于是空清单在这一站的号区间之内，下一站首号在它之上，同号不同内容
/// （<c>SNAPSHOT_REVISION_CONTENT_CONFLICT</c>）在构造上不会出现。
/// </para>
/// <para>
/// <b>为什么不抬旅程的清单基准。</b>计划流重发时抬的是基准（<c>RefreshUpcomingStopPlanAsync</c>），但清单流的基准还决定了<b>已发各版</b>的
/// id：第一版用停靠行上的 id，其余按「停靠 × 号」派生（<see cref="JourneyStopCursor.WorklistMessageIdAt"/>）。抬基准会让已发的首版被算成
/// 派生 id，补发白名单（<c>RuntimeMessageIds</c>）与退役都认不出它。只挪本停靠以后的号，已发各版一个 id 都不变；白名单按
/// <see cref="JourneyStopCursor.WorklistVersionsOf"/> 枚举，多出的这一版自动在里面。
/// </para>
/// <para>
/// <b>与终结同一次改动。</b>只暂存，不保存：空清单、本停靠较早各版的退役、录入请求的结清、号的顺延都随调用方那一次保存落库，崩在中间
/// 不会留下「空清单发了而号没顺延」。保存之后由调用方 <see cref="SendAsync"/>；送不到时留在发件箱，由引擎每轮顶上的补发送出。
/// </para>
/// </remarks>
internal static class StopEndWorklist
{
    private const string WorklistType = "CurrentStopWorklistSnapshot";

    /// <summary>
    /// 终结了 <paramref name="demandId"/> 之后，若它所在的当前停靠已无待做项而旅程继续，暂存那张空清单。返回是否暂存了。
    /// </summary>
    /// <param name="currentSublotRequestMessageId">
    /// 终结之前本停靠在车上那一版录入请求的 id：终结之后按算式已经算不回它，所以由调用方在终结前取好传进来。null 表示没有要结清的。
    /// </param>
    public static async Task<bool> StageAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        string demandId,
        string? currentSublotRequestMessageId,
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
        // 终结的这一条挂在车此刻所在的停靠上，而且这一站因此没有待做项了：这才是「本站结束」。终结的是后面某一站的需求时，
        // 车所在的这一站照旧，清单不动。
        if (staged.AllAtStop(stop).All(item => item.Demand.DemandId != demandId) ||
            staged.OutstandingAtCurrentStop.Count > 0)
        {
            return false;
        }
        SessionRecoveryRow? session = await dbContext.SessionRecoveries.AsNoTracking()
            .SingleOrDefaultAsync(row => row.AgvId == runtime.AgvId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return false;
        }

        long firstAtStop = staged.FirstWorklistRevisionAt(runtime.WorklistRevision, stop);
        long formula = staged.WorklistRevisionAt(runtime.WorklistRevision, stop);
        // 与收尾快照同一道保险：算式万一算低了，也不发一个车上已有的号。
        long revision = Math.Max(
            formula,
            (await JourneyClosure.HighestSentRevisionAsync(dbContext, runtime.AgvId, WorklistType, cancellationToken)
                .ConfigureAwait(false) ?? 0) + 1);

        // 本停靠此前发给车的每一版都退役：终结若一次结束了这一站的几条（期限那一处逐条终结），车上那一版不一定是上一号。
        for (long earlier = firstAtStop; earlier < formula; earlier++)
        {
            await FenceAsync(
                dbContext, staged.WorklistMessageIdAt(runtime.WorklistRevision, stop, earlier), endedAt, cancellationToken)
                .ConfigureAwait(false);
        }

        // 录入请求没人会再答它了。留着没结清，它会被补发进之后每个会话，车上又亮起扫码入口（同收尾那一处的理由）。
        if (currentSublotRequestMessageId is not null)
        {
            ProtocolOutboxRow? entryRequest = await dbContext.ProtocolOutbox
                .SingleOrDefaultAsync(row => row.MessageId == currentSublotRequestMessageId, cancellationToken)
                .ConfigureAwait(false);
            if (entryRequest is not null)
            {
                entryRequest.AcknowledgedAt ??= endedAt;
            }
        }

        string messageId = staged.WorklistMessageIdAt(runtime.WorklistRevision, stop, revision);
        JourneyStopRow tracked = await dbContext.Set<JourneyStopRow>()
            .SingleAsync(row => row.StopId == stop.StopId, cancellationToken).ConfigureAwait(false);
        // 本停靠多占到 revision 这一版为止：之后这一站的「当前号」是 revision + 1（不存在的那一号，同「做完最后一条」），
        // 下一站首号在 revision 之上。
        tracked.WorklistRefills += revision - formula + 1;

        await OnboardJourneyPublisher.StageCurrentStopWorklistAsync(
            new WireToGateStore(dbContext),
            messageId,
            runtime.AgvId,
            session.SessionGeneration,
            new CurrentStopWorklistProjection(stop.StationId, revision, null, null, []),
            endedAt,
            cancellationToken).ConfigureAwait(false);
        // 这一站上已经落库、却没人答的扫码（引擎读收件箱与这把写锁之间到的，或输给了扫码前取消的），同一次改动里答过时。
        await LateSublotSubmission.StageForUnansweredEntriesAsync(
            dbContext, LateSublotSubmission.AddressOf(staged, runtime, stop), revision, session.SessionGeneration, endedAt,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 保存之后：这辆车正在进行的那趟旅程若停在一个已结束的停靠上、它的空清单还没被确认，发出去。没有就什么也不做。
    /// </summary>
    /// <remarks>送不到（没连着、握手中、代次不符、连接正在拆）不算失败，同 <see cref="JourneyClosure.SendAsync"/> 的理由。</remarks>
    public static async Task SendAsync(
        OnboardJourneyPublisher publisher,
        ControlServerDbContext dbContext,
        string agvId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        if (await PendingIdAsync(dbContext, agvId, cancellationToken).ConfigureAwait(false) is not { } messageId)
        {
            return;
        }
        try
        {
            await publisher.SendPersistedAsync(messageId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // 留在发件箱里：引擎每轮顶上的补发按白名单送它。
        }
        // 空清单之后：同一次改动里答过时的那些扫码。
        await LateSublotSubmission.SendStaleAnswersAsync(publisher, dbContext, agvId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> PendingIdAsync(
        ControlServerDbContext dbContext,
        string agvId,
        CancellationToken cancellationToken)
    {
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
            return null;
        }

        JourneyRuntimeRow runtime = await dbContext.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.JourneyId == latest.JourneyId, cancellationToken).ConfigureAwait(false);
        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken)
            .ConfigureAwait(false);
        if (stops.OpenStops.Count == 0 || stops.OutstandingAtCurrentStop.Count > 0 || stops.Current.WorklistRefills == 0)
        {
            return null;
        }

        // 见 StageAsync：暂存之后这一站的「当前号」比空清单多一。
        long revision = stops.WorklistRevisionAt(runtime.WorklistRevision, stops.Current) - 1;
        string messageId = stops.WorklistMessageIdAt(runtime.WorklistRevision, stops.Current, revision);
        string? payloadJson = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageId == messageId)
            .Select(row => row.PayloadJson)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (payloadJson is null)
        {
            return null;
        }
        using JsonDocument document = JsonDocument.Parse(payloadJson);
        return document.RootElement.GetProperty("payload").GetProperty("items").GetArrayLength() == 0 ? messageId : null;
    }

    private static async Task FenceAsync(
        ControlServerDbContext dbContext,
        string messageId,
        DateTimeOffset fencedAt,
        CancellationToken cancellationToken)
    {
        ProtocolOutboxRow? row = await dbContext.ProtocolOutbox
            .SingleOrDefaultAsync(
                item => item.MessageId == messageId && item.AcknowledgedAt == null && item.FencedAt == null,
                cancellationToken)
            .ConfigureAwait(false);
        if (row is not null)
        {
            row.FencedAt = fencedAt;
        }
    }
}
