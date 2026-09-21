using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// Ends a demand at its pickup stop without the cargo leaving it: the demand is terminated, and when it
/// was the journey's last open demand the journey closes under the reason the caller names and the
/// vehicle stops being held by it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The tail every way of ending a pickup stop like this is meant to share.</b> The station
/// departure deadline (ADR-cross-0055, ADR-cross-0058 decision 7) was the first caller. The
/// cancellation before a sublot is entered (ADR-cross-0046, control-server#83) is the second: its
/// ALL_EMPTY result ends the stop through here as <c>CANCELLED_BY_OPERATOR</c>. The settlement of a
/// determinate load failure (control-server#81) ends the stop the same way and differs only in why, so
/// it reuses this rather than release the vehicle a third slightly different way.
/// </para>
/// <para>
/// <b>The endings of a commanded slot operation use it too.</b> Since control-server#131,
/// <c>OnboardRecoveryCoordinator.ApplyCurrentResultAsync</c> ends a demand whose slot operation was
/// commanded -- a cancellation in flight (<c>CANCELLED_BY_OPERATOR</c>), a compensation
/// (<c>CANCELLED_BY_LOAD_COMPENSATION</c>), a fault cargo handoff (<c>TERMINATED_BY_FAULT_CARGO_HANDOFF</c>)
/// -- through here as well, and since control-server#137 a forced mechanical recovery, whose named handoff
/// ends the demand under that same reason. Until then it wrote nearly the same facts by hand, minus the vehicle occupancy,
/// and the pickup order held the vehicle against every later claim. What this still deliberately knows
/// nothing about is the commanded operation itself: the coordinator cancels it, and settles the recovery
/// command it answered, in the same unsaved change. A fault cargo handoff can happen at the gate as well as
/// at the pickup; the tail is the same there, because the occupancy was claimed on the pickup order for the
/// whole journey. It is now the only code that sets a demand <c>Cancelled</c>.
/// </para>
/// <para>
/// <b>上面那句「交接在 gate 也会发生」，在批次7-06（control-server#211）里有一段时间是一条没人处理的预告。</b>
/// 那一票把终结时要结算的录入请求 id 从旅程行改成读<b>当前停靠行</b>（为了清单升版后结算到对的那一条），
/// 而受理只给取货停靠写那一列——于是 gate 上的交接不是被处理，是抛 <c>InvalidDataException</c>，
/// 一条人工介入的恢复路径就此卡死。同一票内修掉了，靠的是
/// <see cref="JourneyStopCursor.CurrentSublotRequestMessageIdOrNone"/>：卸货停靠没有录入请求，
/// 也就没有要结算的那一条。
/// </para>
/// <para>
/// 所以那句话今天的身份是<b>说明</b>而不是警告，判据是
/// <c>RecoveryStateMachineG2Tests.AFaultCargoHandoffAtTheUnloadStopEndsTheDemandLikeOneAtThePickup</c>。
/// 留着这一段，是因为它记着一件值得记的事：<b>那句预告和它的反例住在两个不同的文件里</b>——
/// 这里写着「gate 也会发生」，<c>JourneyRuntimeEngine</c> 里写着「今天只有取货停靠会走到这里」，
/// 单看任何一句都只是普通说明，并排放着才是警报。
/// </para>
/// <para>
/// <b>It stages the changes and does not save.</b> Every fact here has to commit together with the
/// caller's own: a cancelled demand whose vehicle is still leased, or a free vehicle whose journey is
/// still open, is exactly what a crash between two saves would leave behind. The caller saves once.
/// That is also why the vehicle occupancy is released on the tracked order intent rather than through
/// <see cref="Fleet.VehicleDispatchPolicyAccess"/>, whose release saves on its own.
/// </para>
/// <para>
/// <b>Two steps since control-server#207: ending the demand, and closing the journey.</b> Ending the demand
/// touches that demand alone. Closing the journey -- Completed with the reason, the departure wait cleared,
/// the unanswered entry request settled, and the lease, the order occupancy and the purpose claim released
/// together -- happens only when the journey carries no other open demand. A journey still carrying one is
/// left as it is, every occupancy held and every other demand untouched. With one demand per journey, as
/// every journey the runtime creates today, the demand ended is always the last, so both steps run, in the
/// same unsaved change, exactly as the one step did. "The last" is read inside the caller's write
/// transaction (<see cref="DemandJourneyLookup.IsLastOpenDemandAsync"/>); every caller already holds one.
/// </para>
/// <para>
/// <b>批次7-10（control-server#215）起，旅程还没关的那一支多一步：修订计划。</b>终结的需求在后面留下的停靠如果因此没有剩余作业，
/// 就标成已删（REQ-0197），与终结同一次改动暂存。放在这里而不是各个调用方，是因为「任何终结来路之后都要删」：
/// 调用方有四处、两处在引擎里，每处各记得调用一次就是四处会漏。<b>删对每个调用方都成立，换序不是</b>——换序要路网与每区参数，
/// 只有构造时给了 <c>routing</c> 的调用方才换；引擎里那两处（离站超时与确定的装货失败）只 <c>new</c> 了 dbContext，只删不换。
/// </para>
/// <para>
/// <b>It does not decide whether the stop may end.</b> Whether the deadline has passed, whether a slot
/// operation was commanded, whether a door is open: those belong to the caller, because each caller
/// answers them differently.
/// </para>
/// </remarks>
public sealed class PickupStopTermination(ControlServerDbContext dbContext, PlanRevisionRouting? routing = null)
{
    /// <summary>Ends the demand the journey row names, its anchor.</summary>
    public Task StageAsync(
        JourneyRuntimeRow runtime,
        string reasonCode,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return StageAsync(runtime, runtime.DemandId, reasonCode, endedAt, cancellationToken);
    }

    /// <summary>
    /// Ends <paramref name="demandId"/>, a demand <paramref name="runtime"/> carries, and closes the journey if it was
    /// the last open one.
    /// </summary>
    public async Task StageAsync(
        JourneyRuntimeRow runtime,
        string demandId,
        string reasonCode,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);

        await StageDemandTerminationAsync(demandId, cancellationToken).ConfigureAwait(false);
        await StageKeySuppressionAsync(demandId, reasonCode, endedAt, cancellationToken).ConfigureAwait(false);
        if (await DemandJourneyLookup.IsLastOpenDemandAsync(dbContext, runtime.JourneyId, demandId, cancellationToken)
                .ConfigureAwait(false))
        {
            await StageJourneyClosureAsync(runtime, reasonCode, endedAt, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await StagePlanRevisionAsync(runtime, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 同 <see cref="StageAsync(JourneyRuntimeRow, string, string, DateTimeOffset, CancellationToken)"/>，但结算的是
    /// <paramref name="stops"/> 当前停靠上那一版录入请求（批次7-06，control-server#211）。
    /// </summary>
    /// <remarks>
    /// <b>「同名不同源」在这里分岔。</b>旅程行与停靠行上都有一列 <c>SublotRequestMessageId</c>，受理时从前者搬到后者，
    /// 此后一直恒等——直到清单升版：升版换一个新 id，写在<b>停靠行</b>上，而旅程行那一列还停在受理时那个。读旅程行的
    /// 那一版会结算不到当前这条录入请求，于是它被补发进下一个会话，车载端把它当成内容已变的业务 id 而断会话。
    /// 所以有游标时读游标；没有游标的那个重载留给调用方没有停靠上下文的路径，它读旅程行，而那条路径只在单需求旅程上
    /// 走得到，两者恒等。
    /// </remarks>
    public async Task StageAsync(
        JourneyRuntimeRow runtime,
        string? currentSublotRequestMessageId,
        string demandId,
        string reasonCode,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);

        await StageDemandTerminationAsync(demandId, cancellationToken).ConfigureAwait(false);
        await StageKeySuppressionAsync(demandId, reasonCode, endedAt, cancellationToken).ConfigureAwait(false);
        if (await DemandJourneyLookup.IsLastOpenDemandAsync(dbContext, runtime.JourneyId, demandId, cancellationToken)
                .ConfigureAwait(false))
        {
            await StageJourneyClosureAsync(
                runtime, currentSublotRequestMessageId, reasonCode, endedAt, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await StagePlanRevisionAsync(runtime, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>旅程还带着别的需求时，删掉终结留下的空停靠（给了路网时还换序）。见类注释里批次7-10 那一段。</summary>
    private Task<PlanRevisionResult> StagePlanRevisionAsync(JourneyRuntimeRow runtime, CancellationToken cancellationToken) =>
        JourneyPlanRevisionStage.StageAsync(
            dbContext, runtime.JourneyId, leavingDemandIds: [], currentStopMayGo: false, routing, cancellationToken);

    /// <summary>
    /// 本地取消的四个终态码（<c>REQ-0156</c>）。只有它们按业务键抑制；<c>Succeeded</c>、GONE（v2 没有这条路径）与
    /// <c>TERMINATED_BY_FAULT_CARGO_HANDOFF</c> 不抑制——需求基线只列这四个，MVP 多写的第五个（<c>557644a6</c>）不移植。
    /// </summary>
    /// <remarks>
    /// <c>CANCELLED_BY_STOP_COMPLETE</c> 今天在 <c>src/</c> 里没有生产者，放在这里是为了以后谁产生它谁就自动抑制，
    /// 不必再有人记得回到这里加一行。
    /// </remarks>
    public static IReadOnlySet<string> KeySuppressingReasonCodes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "CANCELLED_BY_OPERATOR",
        "CANCELLED_BY_LOAD_COMPENSATION",
        "CANCELLED_BY_STOP_COMPLETE",
        "CANCELLED_BY_STATION_TIMEOUT",
    };

    /// <summary>
    /// 终结的是本地取消时，按这条需求的业务键暂存一条抑制（批次7-05，control-server#210；REQ-0155、REQ-0156、REQ-0211）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>暂存、不保存</b>，与终态、租约、旅程收尾同一次保存：崩在两次保存之间会留下「已取消但键未抑制」，而那正是 MES 以新
    /// <c>DemandId</c> 再发同一个键时会被再执行一次的样子。
    /// </para>
    /// <para>
    /// <b>先写者胜、不覆盖、不抛。</b>键上已有抑制（本上下文暂存的，或库里已提交的）就什么也不做。这一读与「是不是最后一条」
    /// 同样落在调用方的写事务里（见类注释），而这台服务端的写事务是 <c>BEGIN IMMEDIATE</c>：两个写者——站点期限与取消结果——
    /// 串行，后者一定读到前者已提交的那一条。所以这里不靠主键冲突兜底：主键冲突发生在保存时，会把同一次保存里的终态一起拒掉。
    /// </para>
    /// <para>
    /// 不经 <see cref="ITransportDemandSuppressionStore.SuppressIfAbsentAsync"/>：那个端口自己保存，而这里的全部事实必须等调用方
    /// 那一次保存。行的形状与端口写的一致。
    /// </para>
    /// </remarks>
    private async Task StageKeySuppressionAsync(
        string demandId, string reasonCode, DateTimeOffset endedAt, CancellationToken cancellationToken)
    {
        if (!KeySuppressingReasonCodes.Contains(reasonCode))
        {
            return;
        }

        AcceptedDemandRow demand = await dbContext.AcceptedDemands
            .SingleAsync(row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
        string key = demand.TransportDemandKey;
        if (dbContext.Set<TransportDemandSuppressionRow>().Local.Any(row => row.TransportDemandKey == key) ||
            await dbContext.Set<TransportDemandSuppressionRow>().AsNoTracking()
                .AnyAsync(row => row.TransportDemandKey == key, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        dbContext.Set<TransportDemandSuppressionRow>().Add(new TransportDemandSuppressionRow
        {
            TransportDemandKey = key,
            DemandId = demandId,
            ReasonCode = reasonCode,
            SuppressedAt = endedAt,
        });
    }

    /// <summary>
    /// The first step: this demand, and nothing else, is terminated. Its membership in the journey is marked
    /// terminated too (control-server#211), which is what takes it off the stop's worklist.
    /// </summary>
    public async Task StageDemandTerminationAsync(string demandId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        AcceptedDemandRow demand = await dbContext.AcceptedDemands
            .SingleAsync(row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
        if (demand.Status == DemandExecutionStatus.Succeeded)
        {
            throw new BusinessIdentityConflictException(
                "A completed demand cannot be terminated at its pickup stop.");
        }
        demand.Status = DemandExecutionStatus.Cancelled;
        JourneyDemandRow? membership = await dbContext.Set<JourneyDemandRow>()
            .SingleOrDefaultAsync(row => row.DemandId == demandId && row.RemovedAt == null, cancellationToken)
            .ConfigureAwait(false);
        if (membership is not null)
        {
            membership.Status = JourneyDemandStatuses.Terminated;
        }
    }

    /// <summary>
    /// The second step, for a journey that carries no open demand any more: it completes under
    /// <paramref name="reasonCode"/>, and the lease, the order occupancy and the purpose claim are released together.
    /// </summary>
    public Task StageJourneyClosureAsync(
        JourneyRuntimeRow runtime,
        string reasonCode,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return StageJourneyClosureAsync(
            runtime, runtime.SublotRequestMessageId, reasonCode, endedAt, cancellationToken);
    }

    /// <inheritdoc cref="StageJourneyClosureAsync(JourneyRuntimeRow, string, DateTimeOffset, CancellationToken)"/>
    /// <remarks>
    /// <paramref name="currentSublotRequestMessageId"/> 是本停靠此刻那一版录入请求，见上面那段「同名不同源」。
    /// </remarks>
    public async Task StageJourneyClosureAsync(
        JourneyRuntimeRow runtime,
        string? currentSublotRequestMessageId,
        string reasonCode,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);

        await JourneyLeaseRelease.StageAsync(dbContext, runtime.JourneyId, endedAt, cancellationToken)
            .ConfigureAwait(false);
        OrderIntentRow pickup = await dbContext.OrderIntents
            .SingleAsync(row => row.UpperId == runtime.PickupUpperId, cancellationToken).ConfigureAwait(false);
        pickup.VehicleOccupancyReleasedAt ??= endedAt;

        // Nobody is going to answer the entry request now. Left unsettled it is replayed into every
        // later session, where the peer refuses it as a business id whose content changed and tears
        // the session down -- the same failure the answered request is settled for.
        // null 的意思是「这个停靠本来就没有录入请求」，不是「读不到」——见
        // JourneyStopCursor.CurrentSublotRequestMessageIdOrNone。空字符串则是调用方给错了，照常查、照常查不到。
        ProtocolOutboxRow? entryRequest = currentSublotRequestMessageId is null
            ? null
            : await dbContext.ProtocolOutbox
                .SingleOrDefaultAsync(row => row.MessageId == currentSublotRequestMessageId, cancellationToken)
                .ConfigureAwait(false);
        if (entryRequest is not null)
        {
            entryRequest.AcknowledgedAt ??= endedAt;
        }

        runtime.Stage = JourneyRuntimeStage.Completed;
        runtime.SetBlockReason(reasonCode, endedAt);
        runtime.StationDepartureWaitStartedAt = null;
        runtime.UpdatedAt = endedAt;
    }
}
