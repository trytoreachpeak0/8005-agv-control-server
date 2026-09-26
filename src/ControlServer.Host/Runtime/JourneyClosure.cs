using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 一趟旅程变成 <see cref="JourneyRuntimeStage.Completed"/> 的唯一出口，以及车被告知「这一趟结束了」的唯一出处（control-server#323）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要告诉车。</b>车上显示的清单、计划与业务状态都是服务端推过去的快照，只会被更高修订号的同类快照替换。旅程收尾之前
/// 这里什么都不发，车于是一直留着已经结束的那一站：清单、录入请求、「取消装货」按钮、装货阶段那一行（program#86，agv01 09-15）。
/// 而且车载端把采纳的旅程快照写进本地日志库、每次重连前恢复（onboard-hmi <c>RestorePersistedJourneyProjectionAsync</c>），
/// 所以断线救不了它。
/// </para>
/// <para>
/// <b>为什么是唯一出口。</b>来路有六条——站点期限、确定的装货失败、扫码前取消、已下命令后的几种结束、到站之前的释放、正常卸完——
/// 前五条经 <see cref="PickupStopTermination"/> 的收尾尾巴，第六条在引擎里。把「写 Completed」与「暂存收尾快照」放进同一个方法，
/// 「关了旅程却没告诉车」就在构造上不可能；<c>JourneyClosureSingleExitArchitectureTests</c> 守着「只有这里写 Completed」。
/// </para>
/// <para>
/// <b>三张快照。</b>清单：<c>items</c> 为空、作业会话与离站期限为 null，站点取车停在（或正驶向）的那个停靠。计划：<c>legs</c> 为空，
/// 与现场线一致。业务状态：<c>activePurpose</c> 与 <c>loadingPhase</c> 为 null——不发它，装货阶段那一行（持货倒计时、
/// 「本站计划装货已完成」）会一直挂着（onboard-hmi <c>LoadingPhaseText</c>）。协议 v2.0.0 都能表达：清单项、计划腿都没有下限。
/// </para>
/// <para>
/// <b>修订号不动旅程的基准。</b>现场线收尾时 <c>WorklistRevision++</c>；v2 里那一列是整趟旅程的基准，挪了它整趟的号都跟着挪。
/// 清单取 <see cref="JourneyStopCursor.WorklistRevisionAt"/>（本停靠刚终结的那一条算作做完，所以比车上那一版大一），计划与业务状态取
/// 「发件箱里这辆车这条流上最大的那一版 + 1」（同 <c>RefreshUpcomingStopPlanAsync</c> 的做法）。按车计数器由暂存原语推高
/// （<see cref="WireToGateStore.RaiseSnapshotRevisionFloorAsync"/>），下一趟受理时的基准因此一定在收尾号之上。
/// </para>
/// <para>
/// <b>只暂存，不保存；保存之后由调用方发。</b>与收尾的其余事实同一次保存（<see cref="PickupStopTermination"/> 类注释那条理由），
/// 然后 <see cref="SendAsync"/>。发不出去（没连着、握手中、代次对不上）不算失败：那几行留在发件箱里，车重连、恢复报告被答复之后
/// 由 <see cref="ReplayIdsAsync"/> 补发。这辆车一旦有了下一趟旅程就不再补发——那一趟的快照号更高，旧的收尾快照补进去就是回退。
/// </para>
/// <para>
/// <b>不看会话就不就绪。</b>真车载端开往取货站的全程都因本服务端自己的在途单而未就绪（<c>DEPARTURE_SAFETY_NOT_READY</c>，
/// control-server#314），到站之前的释放恰恰发生在那时。车载端收行程快照不看自己的就绪状态，所以照样当场发。
/// </para>
/// </remarks>
internal static class JourneyClosure
{
    private const string WorklistType = "CurrentStopWorklistSnapshot";
    private const string PlanType = "UpcomingStopPlanSnapshot";
    private const string BusinessType = "VehicleBusinessStateSnapshot";

    /// <summary>
    /// 旅程收尾：阶段写成 Completed、阻断原因写成 <paramref name="reasonCode"/>（正常卸完为 null），并把三张收尾快照暂存进同一次改动。
    /// </summary>
    public static async Task StageAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        string? reasonCode,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(runtime);

        runtime.Stage = JourneyRuntimeStage.Completed;
        runtime.SetBlockReason(reasonCode, endedAt);
        runtime.UpdatedAt = endedAt;
        await StageSnapshotsAsync(dbContext, runtime, endedAt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>这趟旅程三张收尾快照的 messageId。一趟旅程只收尾一次，所以按旅程派生。</summary>
    public static IReadOnlyList<string> SnapshotMessageIds(string journeyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        return
        [
            JourneyPlanBuilder.StableGuid(journeyId, "closure-worklist"),
            JourneyPlanBuilder.StableGuid(journeyId, "closure-plan"),
            JourneyPlanBuilder.StableGuid(journeyId, "closure-vehicle-business-state")
        ];
    }

    /// <summary>
    /// 保存之后：把这辆车最近一趟旅程的收尾快照里还没被确认的发出去。最近一趟不是收尾了的，什么也不做。
    /// </summary>
    /// <remarks>
    /// 送不到只意味着「这一刻车不在线上」：<c>OnboardPeer</c> 对没连着、握手中、代次对不上的车一律抛 <see cref="IOException"/>，
    /// 而那三种情形都由重连之后的补发接着送（<see cref="ReplayIdsAsync"/>）。所以这里吞掉它，不让一次已经提交的收尾在事后变成一轮失败。
    /// 连接恰好正在被拆掉时，它的发送闸门或底层流已经释放，抛的是 <see cref="ObjectDisposedException"/>：
    /// 那也是「这一刻车不在线上」，同样吞掉（PR #329 审查，低 2）。别的异常照常冒出去。
    /// </remarks>
    public static async Task SendAsync(
        OnboardJourneyPublisher publisher,
        ControlServerDbContext dbContext,
        string agvId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        foreach (string messageId in await ReplayIdsAsync(dbContext, agvId, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await publisher.SendPersistedAsync(messageId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // 见 remarks：留在发件箱里，等重连补发。
            }
        }
    }

    /// <summary>
    /// 这辆车此刻有权补发的收尾快照：最近一趟旅程（按受理时刻）已经收尾时，它那几张落了库的；否则一张都没有。
    /// </summary>
    /// <remarks>
    /// 「最近一趟」在客户端排：SQLite 不接受 <see cref="DateTimeOffset"/> 的 ORDER BY，而一辆车的旅程行按条数算。
    /// 补发本身（<see cref="OnboardJourneyPublisher.ReplayPendingForSessionAsync"/>）只发没确认、没退役的行，所以这里给的是
    /// 「有权发的」，不是「该发的」。
    /// </remarks>
    public static async Task<IReadOnlyList<string>> ReplayIdsAsync(
        ControlServerDbContext dbContext,
        string agvId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        var journeys = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.AgvId == agvId)
            .Select(row => new { row.JourneyId, row.Stage, row.CreatedAt })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var latest = journeys
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.JourneyId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (latest is null || latest.Stage != JourneyRuntimeStage.Completed)
        {
            return [];
        }

        string[] ids = [.. SnapshotMessageIds(latest.JourneyId)];
        string[] stored = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => ids.Contains(row.MessageId))
            .Select(row => row.MessageId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return [.. ids.Where(stored.Contains)];
    }

    private static async Task StageSnapshotsAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        // 车从没和这台服务端建过会话：它手上没有这一趟的任何东西，也没有一代会话可以盖在报文上。
        SessionRecoveryRow? session = await dbContext.SessionRecoveries
            .SingleOrDefaultAsync(row => row.AgvId == runtime.AgvId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return;
        }

        JourneyStopRow closingStop = await ClosingStopAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
        JourneyStopCursor staged = await JourneyStopCursor
            .LoadIncludingUnsavedChangesAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
        JourneyStopRow stop = staged.Stops.Single(row => row.StopId == closingStop.StopId);
        // 公式之外再与「这条流上发过的最大号 + 1」取大：公式对的时候两者不冲突（本停靠刚终结的那条让它比车上那一版大），
        // 万一哪条来路让公式算低了，车收到的也不会是回退或同号不同内容——那两种都会当场拆会话。
        long worklistRevision = Math.Max(
            staged.WorklistRevisionAt(runtime.WorklistRevision, stop),
            (await HighestSentRevisionAsync(dbContext, runtime.AgvId, WorklistType, cancellationToken).ConfigureAwait(false) ?? 0) + 1);
        long planRevision =
            await HighestSentRevisionAsync(dbContext, runtime.AgvId, PlanType, cancellationToken).ConfigureAwait(false) + 1
            ?? runtime.PlanRevision;
        long businessRevision =
            await HighestSentRevisionAsync(dbContext, runtime.AgvId, BusinessType, cancellationToken).ConfigureAwait(false) + 1
            ?? runtime.VehicleBusinessRevision;

        WireToGateStore store = new(dbContext);
        IReadOnlyList<string> ids = SnapshotMessageIds(runtime.JourneyId);
        await OnboardJourneyPublisher.StageCurrentStopWorklistAsync(
            store,
            ids[0],
            runtime.AgvId,
            session.SessionGeneration,
            new CurrentStopWorklistProjection(closingStop.StationId, worklistRevision, null, null, []),
            endedAt,
            cancellationToken).ConfigureAwait(false);
        await OnboardJourneyPublisher.StageUpcomingStopPlanAsync(
            store,
            ids[1],
            runtime.AgvId,
            session.SessionGeneration,
            new UpcomingStopPlanProjection(planRevision, []),
            endedAt,
            cancellationToken).ConfigureAwait(false);
        await OnboardJourneyPublisher.StageVehicleBusinessStateAsync(
            store,
            ids[2],
            runtime.AgvId,
            session.SessionGeneration,
            // 与引擎的运输业务状态同一组值（JourneyRuntimeEngine.TransportBusinessState），只是不再有旅程：用途与装货阶段为空。
            new VehicleBusinessProjection(businessRevision, "READY", null, false, "SUFFICIENT", "NOT_CHARGING", null, []),
            endedAt,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 车在（或正驶向）的那个停靠：库里序位最小的、还开着的那一个；都完成了（正常卸完）就是最后完成的那一个。
    /// </summary>
    /// <remarks>
    /// 读库而不读本次改动：这次改动可能刚把停靠标成已删（到站之前的释放连同当前停靠一起删），而清单要说的是改动之前车所在的那一站。
    /// </remarks>
    private static async Task<JourneyStopRow> ClosingStopAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        CancellationToken cancellationToken)
    {
        JourneyStopRow[] stops = await dbContext.Set<JourneyStopRow>().AsNoTracking()
            .Where(row => row.JourneyId == runtime.JourneyId)
            .OrderBy(row => row.Sequence)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return stops.FirstOrDefault(row => row.Status is not (JourneyStopStatuses.Completed or JourneyStopStatuses.Removed))
            ?? stops.LastOrDefault(row => row.Status == JourneyStopStatuses.Completed)
            ?? stops.LastOrDefault()
            ?? throw new InvalidDataException($"Journey '{runtime.JourneyId}' has no stops.");
    }

    /// <summary>发件箱里这辆车这条快照流上最大的修订号；一张都没有时为 null。确认与否、退役与否都算：车可能已经收到了。</summary>
    /// <remarks>
    /// <para>
    /// 发件箱行没有 agvId 这一列，按载荷逐行解析着筛。<b>不在库里按 agvId 子串粗筛</b>：载荷是转义过的 JSON，车号里的非 ASCII
    /// 字符在里面是 Unicode 转义序列，子串对不上就一行都筛不出来——而「一张都没有」在这里会退回旅程的基准，静默地发出一个
    /// 可能与车上那一版同号的快照。第一版就是这么写错的（测试夹具的车号是中文），<c>ClosureSnapshotAssertions</c> 的
    /// 「号比这条流上别的每一版都大」抓到了它。
    /// </para>
    /// <para>
    /// 代价是每次收尾把这三类快照整表读一遍。一趟旅程只收尾一次；引擎的 <c>LastSentPlanAsync</c> 对追加过的旅程每轮都这么读。
    /// </para>
    /// </remarks>
    internal static async Task<long?> HighestSentRevisionAsync(
        ControlServerDbContext dbContext,
        string agvId,
        string messageType,
        CancellationToken cancellationToken)
    {
        string revisionProperty = messageType switch
        {
            WorklistType => "worklistRevision",
            PlanType => "planRevision",
            _ => "vehicleBusinessStateRevision"
        };
        string[] payloads = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == messageType)
            .Select(row => row.PayloadJson)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        long? highest = null;
        foreach (string payloadJson in payloads)
        {
            using JsonDocument document = JsonDocument.Parse(payloadJson);
            JsonElement root = document.RootElement;
            if (root.GetProperty("agvId").GetString() != agvId)
            {
                continue;
            }

            long revision = root.GetProperty("payload").GetProperty(revisionProperty).GetInt64();
            highest = highest is { } seen ? Math.Max(seen, revision) : revision;
        }

        return highest;
    }
}
