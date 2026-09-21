using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Release;

/// <summary>一次释放尝试的结果，给日志与测试看。</summary>
public sealed record DemandReleaseOutcome(string JourneyId, string DemandId, string Trigger, string Result);

/// <summary>
/// REQ-0328：仅当前车不合格时释放改派（集合 B，仅限未取货；批次7-10，control-server#215）。
/// </summary>
/// <remarks>
/// <para>
/// <b>一轮做三件事</b>：对每趟没结束的旅程、每条还没取货的需求问一次「这辆车对它还合不合格」
/// （<see cref="DemandReleaseRules.VehicleNoLongerEligible"/>）；不合格就裁决能不能释放（<see cref="DemandReleaseRules.Decide"/>）；
/// 能就先确认它的取货腿在 RIoT 上没有订单，再在一个写事务里释放、退回积压、修订计划。
/// </para>
/// <para>
/// <b>「没有订单」在写事务里判，不用这一轮开头的读数</b>（票面点名的并发读改写）。车正开往它的取货站时，那张单先经订单命令面
/// 取消并对账——命令面自己先武装审计行再发（<see cref="RiotOrderCommandService"/>），所以取消发出而释放没落库时崩溃，
/// 下一轮读到那条审计行就只对账、不再发第二次取消。<b>结果不是 Confirmed 就不释放</b>：Pending 下一轮再对账，
/// Failed 与 Unknown 写阻断原因，需求与车保持原状。取消必须在暂存任何改动之前发：审计存储自己保存，会把上下文里暂存的一并提交。
/// </para>
/// <para>
/// <b>释放与退回积压同一事务</b>：归属标移除、积压行清掉受理标记（<see cref="JourneyBacklogRow.FirstSeenAt"/> 不动，等待年龄因此
/// 保留）、计划修订暂存，一次保存。旅程上没有别的未结需求时按批次7-02 关闭旅程与三套占用
/// （<see cref="PickupStopTermination.StageJourneyClosureAsync(JourneyRuntimeRow, string, DateTimeOffset, CancellationToken)"/>）。
/// 仓位不用单独归还：占用由 <c>JourneyAwareSlotLedger</c> 按未移除的归属算。
/// </para>
/// <para>
/// <b>到站已记录则不释放</b>，在写事务里再判一次：取货站到站与释放同时发生时，到站那一侧先提交的话这里读到的已经不是
/// 「开往取货站」了。
/// </para>
/// </remarks>
public sealed class DemandReleaseService(
    ControlServerDbContext dbContext,
    IVehicleFaultStore faults,
    VehicleDispatchPolicyAccess dispatchPolicy,
    IRiotVehicleFacts vehicleFacts,
    RiotOrderCommandService orderCommands,
    IRiotOrderCommandAuditStore commandAudit,
    PlanRevisionRoutingSource routingSource,
    IOptions<JourneyRuntimeOptions> options,
    TimeProvider timeProvider,
    ILogger<DemandReleaseService> logger)
{
    public const string CancelCommandType = "CANCEL";

    private static readonly Action<ILogger, string, string, string, Exception?> LogReleased =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(2150, nameof(LogReleased)),
            "Demand {DemandId} was released from journey {JourneyId} for redispatch: the vehicle is no longer eligible ({Trigger}).");

    private static readonly Action<ILogger, string, string, string, string, Exception?> LogRefused =
        LoggerMessage.Define<string, string, string, string>(
            LogLevel.Warning,
            new EventId(2151, nameof(LogRefused)),
            "Demand {DemandId} of journey {JourneyId} was not released although the vehicle is no longer eligible ({Trigger}): {Reason}.");

    private static readonly Action<ILogger, string, string, string, string, Exception?> LogRefusedAgain =
        LoggerMessage.Define<string, string, string, string>(
            LogLevel.Debug,
            new EventId(2154, nameof(LogRefusedAgain)),
            "Demand {DemandId} of journey {JourneyId} is still not released ({Trigger}): {Reason}.");

    private static readonly Action<ILogger, string, string, Exception?> LogRefusalCleared =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2155, nameof(LogRefusalCleared)),
            "Journey {JourneyId} no longer carries release refusal {Reason}: no demand of it is judged ineligible any more.");

    private static readonly Action<ILogger, string, string, Exception?> LogVehicleUnread =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2152, nameof(LogVehicleUnread)),
            "The RIoT observation of vehicle {VehicleKey} (journey {JourneyId}) could not be read; the map is not judged this round.");

    private readonly JourneyRuntimeOptions _options = options.Value;

    public async Task<IReadOnlyList<DemandReleaseOutcome>> RunOnceAsync(CancellationToken cancellationToken)
    {
        VehicleDispatchPolicy policy = await dispatchPolicy.EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
        JourneyRuntimeRow[] journeys = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.Stage != JourneyRuntimeStage.Completed)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        List<DemandReleaseOutcome> outcomes = [];
        foreach (JourneyRuntimeRow journey in journeys)
        {
            outcomes.AddRange(await RunForJourneyAsync(journey, policy, cancellationToken).ConfigureAwait(false));
        }

        return outcomes;
    }

    private async Task<IReadOnlyList<DemandReleaseOutcome>> RunForJourneyAsync(
        JourneyRuntimeRow journey,
        VehicleDispatchPolicy policy,
        CancellationToken cancellationToken)
    {
        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, journey, cancellationToken).ConfigureAwait(false);
        JourneyStopDemand[] waiting = [.. stops.Demands.Where(item =>
            item.Membership.Status == JourneyDemandStatuses.PendingLoad)];
        if (waiting.Length == 0)
        {
            // 没有待装的需求就没有东西可释放：之前被拒时写下的码不再说明任何事。
            await ClearRefusalAsync(journey, cancellationToken).ConfigureAwait(false);
            return [];
        }

        VehicleFaultFact? fault = await faults.ReadAsync(journey.AgvId, cancellationToken).ConfigureAwait(false);
        RiotVehicleObservation? observation = await ReadVehicleAsync(journey, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        List<DemandReleaseOutcome> outcomes = [];
        foreach (JourneyStopDemand item in waiting)
        {
            string? trigger = DemandReleaseRules.VehicleNoLongerEligible(
                fault, policy, journey.AgvId, item.Demand.WorkType, item.Membership.DispatchZone, observation, now, _options);
            if (trigger is null)
            {
                continue;
            }

            outcomes.Add(await ReleaseOneAsync(journey, stops, item, trigger, fault, cancellationToken).ConfigureAwait(false));
            // 释放改了这趟旅程的停靠与归属：下一条要按新的样子判，而不是按这一轮开头读到的。
            stops = await JourneyStopCursor.LoadAsync(dbContext, journey, cancellationToken).ConfigureAwait(false);
        }

        // 车重新合格：之前被拒时写下的码清掉（审查 M4）。只在确实读到了在线的车时才判「重新合格」——读不到时观测派生的判据
        // 不判（规则里的门），没触发不等于车好了，清掉只会让下一轮再写一次、阻断开始时刻每轮重置。
        if (outcomes.Count == 0 && observation is { Connected: true })
        {
            await ClearRefusalAsync(journey, cancellationToken).ConfigureAwait(false);
        }

        return outcomes;
    }

    private async Task<DemandReleaseOutcome> ReleaseOneAsync(
        JourneyRuntimeRow journey,
        JourneyStopCursor stops,
        JourneyStopDemand item,
        string trigger,
        VehicleFaultFact? fault,
        CancellationToken cancellationToken)
    {
        string demandId = item.Membership.DemandId;
        bool pickupIsCurrent = string.Equals(stops.Current.StopId, item.Membership.PickupStopId, StringComparison.Ordinal);
        DemandReleaseDecision decision = DemandReleaseRules.Decide(
            item.Membership.Status,
            isAnchor: string.Equals(journey.DemandId, demandId, StringComparison.Ordinal),
            otherOpenDemands: stops.Demands.Count(other =>
                !string.Equals(other.Membership.DemandId, demandId, StringComparison.Ordinal)),
            pickupIsCurrentNextStop: pickupIsCurrent,
            arrivedAtCurrentNextStop: journey.Stage != JourneyRuntimeStage.AwaitingPickupArrival);

        switch (decision.Action)
        {
            case DemandReleaseAction.Refuse:
                return await RefuseAsync(journey, demandId, trigger, decision.RefusalReason!, cancellationToken)
                    .ConfigureAwait(false);
            case DemandReleaseAction.CancelPickupOrderThenRelease:
                (PickupOrderSettlement settlement, string? notSettled) = await CancelPickupOrderAsync(
                    journey, stops.Current, fault, cancellationToken).ConfigureAwait(false);
                if (notSettled is not null)
                {
                    return await RefuseAsync(journey, demandId, trigger, notSettled, cancellationToken)
                        .ConfigureAwait(false);
                }

                return await ReleaseAsync(
                        journey, demandId, trigger,
                        pickupOrderSettled: settlement is PickupOrderSettlement.Cancelled or PickupOrderSettlement.Ended,
                        cancellationToken)
                    .ConfigureAwait(false);
            case DemandReleaseAction.ReleaseWithoutOrder:
                return await ReleaseAsync(journey, demandId, trigger, pickupOrderSettled: false, cancellationToken)
                    .ConfigureAwait(false);
            default:
                return new DemandReleaseOutcome(journey.JourneyId, demandId, trigger, "NOT_APPLICABLE");
        }
    }

    /// <summary>
    /// 了结这趟旅程开往当前下一站的那张单：没有活订单、或取消并对账确认了，返回怎么了结的；否则返回不释放的原因。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 先看有没有取消过：审计里有这张单的取消尝试就只对账那一次，不发第二次（崩溃点：取消已发出而释放未落库）。
    /// </para>
    /// <para>
    /// <b>「没有订单」只认确定的那一种</b>（审查 S2）：意图不在，或者意图还在初始的待对账状态、一次创建都没发出过
    /// （<see cref="NeverDispatched"/>）。意图上没有订单号并不等于没有订单——创建已发出而回应丢了（CREATE_ATTEMPTED、
    /// RESULT_UNKNOWN）时 RIoT 上可能已经有一张活的，而没有订单号就发不了取消。那种情况不释放，等引擎把意图对账出结论。
    /// </para>
    /// <para>
    /// <b>取消之前先读订单</b>（复审中 2）：已经是终态的单没有东西可取消。取消、失败、删除、挂起算「没有活订单」，不发取消、
    /// 直接释放；SUCCESS 说明车已经到了取货站（引擎可能还没记下），不释放。旧实现有订单号就发 CANCEL，对 FAILED 单的取消
    /// 对账为 Failed，之后每轮对已结的 Failed 只返回、不重读，永远停在「取消没确认」。
    /// </para>
    /// <para>
    /// <b>故障协调器 Hold 住的单不取消</b>（复审疑问，调度定 B）：故障唯一的清除路径 <c>VehicleFaultCoordinator.ResumeAsync</c>
    /// 要求订单 HELD，释放取消它就把那条路拆了（cs#299）。判据只看审计：本故障代次在这张单上有一次 Hold 尝试，而且结果不是
    /// Failed——Confirmed 是 HELD；Pending 与 Unknown 是「可能 HELD」，一律算在内，偏保守。它排在终态判断之后：终态单上没有
    /// 东西能被 Hold 住（对 FAILED 单的 Hold 必然判 Failed），今天唯一自动产生故障的来源（订单 FAILED）因此照常释放。
    /// </para>
    /// </remarks>
    private async Task<(PickupOrderSettlement Settlement, string? NotSettled)> CancelPickupOrderAsync(
        JourneyRuntimeRow journey,
        JourneyStopRow currentStop,
        VehicleFaultFact? fault,
        CancellationToken cancellationToken)
    {
        OrderIntentRow? intent = await dbContext.OrderIntents.AsNoTracking()
            .SingleOrDefaultAsync(row => row.UpperId == currentStop.UpperId, cancellationToken).ConfigureAwait(false);
        if (NeverDispatched(intent))
        {
            return (PickupOrderSettlement.NoOrder, null);
        }

        if (intent!.OrderId is not { } orderId)
        {
            return (PickupOrderSettlement.None, DemandReleaseReasons.OrderStateUnknown);
        }

        // 先对账已经发过的取消（崩溃点：取消已发出而释放未落库）。放在读订单之前：读到 CANCELLED 就当「没有活订单」直接放，
        // 会让那一次取消的审计行永远停在 Pending——服务端明明知道取消成了，记录却说不知道（L2-RVI-04 抓到）。
        IReadOnlyList<RiotOrderCommandAttempt> attempts = await commandAudit
            .ReadAttemptsAsync(CancelCommandType, currentStop.UpperId, cancellationToken).ConfigureAwait(false);
        if (attempts.Count > 0 &&
            await orderCommands.ReconcileAsync(attempts[^1], cancellationToken).ConfigureAwait(false)
                == RiotOrderCommandOutcome.Confirmed)
        {
            return (PickupOrderSettlement.Cancelled, null);
        }

        RiotOrderObservation order = await vehicleFacts
            .ReconcileByUpperIdAsync(currentStop.UpperId, cancellationToken).ConfigureAwait(false);
        switch (order.OrderState)
        {
            case RiotOrderState.Cancelled or RiotOrderState.Failed or RiotOrderState.Deleted or RiotOrderState.Suspended:
                return (PickupOrderSettlement.Ended, null);
            case RiotOrderState.Success:
                return (PickupOrderSettlement.None, DemandReleaseReasons.PickupOrderSucceeded);
        }

        if (fault is { Level: not VehicleFaultLevel.None } &&
            (await commandAudit.ReadAttemptsAsync(RiotCommandTypeNames.OrderHold, currentStop.UpperId, cancellationToken)
                .ConfigureAwait(false))
            .Any(attempt => attempt.FaultGeneration == fault.FaultGeneration &&
                            attempt.Outcome != RiotOrderCommandOutcome.Failed))
        {
            return (PickupOrderSettlement.None, DemandReleaseReasons.FaultHoldInEffect);
        }

        // 订单还活着：发过取消的只对账、不发第二次（重发属 cs#296）；没发过的发一次并对账。
        if (attempts.Count > 0)
        {
            return (PickupOrderSettlement.None, DemandReleaseReasons.OrderCancelNotConfirmed);
        }

        RiotOrderCommandOutcome outcome = (await orderCommands.IssueAsync(
                RiotOrderCommandKind.Cancel,
                new RiotOrderCommandTarget(journey.AgvId, currentStop.UpperId, orderId),
                "REQ-0328 release for redispatch: the vehicle is no longer eligible for this demand",
                faultGeneration: null,
                cancellationToken).ConfigureAwait(false)).Outcome;
        return outcome == RiotOrderCommandOutcome.Confirmed
            ? (PickupOrderSettlement.Cancelled, null)
            : (PickupOrderSettlement.None, DemandReleaseReasons.OrderCancelNotConfirmed);
    }

    /// <summary>
    /// 这张单确定从没向 RIoT 发出过创建：意图不在，或者还在初始的待对账状态、创建次数为零、没有订单号。
    /// 其余一切状态都可能在 RIoT 上有一张活的订单。
    /// </summary>
    private static bool NeverDispatched(OrderIntentRow? intent) =>
        intent is null ||
        (intent.Status == "PENDING_RECONCILIATION" && intent.CreateAttemptCount == 0 && intent.OrderId is null);

    private async Task<DemandReleaseOutcome> ReleaseAsync(
        JourneyRuntimeRow journey,
        string demandId,
        string trigger,
        bool pickupOrderSettled,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using IDbContextTransaction? transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        dbContext.ChangeTracker.Clear();
        JourneyRuntimeRow runtime = await dbContext.JourneyRuntimes
            .SingleAsync(row => row.JourneyId == journey.JourneyId, cancellationToken).ConfigureAwait(false);
        JourneyDemandRow? membership = await dbContext.Set<JourneyDemandRow>()
            .SingleOrDefaultAsync(
                row => row.JourneyId == journey.JourneyId && row.DemandId == demandId && row.RemovedAt == null,
                cancellationToken).ConfigureAwait(false);
        // 这一轮开头读完之后它可能已经装上了（集合 A）、被终结了，或者旅程已经结束：都不再是本条的事。
        if (membership is null || membership.Status != JourneyDemandStatuses.PendingLoad ||
            runtime.Stage == JourneyRuntimeStage.Completed)
        {
            return new DemandReleaseOutcome(journey.JourneyId, demandId, trigger, "NO_LONGER_APPLICABLE");
        }

        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
        JourneyStopRow pickup = stops.Stops.Single(row => row.StopId == membership.PickupStopId);
        if (string.Equals(stops.Current.StopId, pickup.StopId, StringComparison.Ordinal))
        {
            // 到站已记录则不释放；开往它的那张单也必须是刚刚确认取消的那一张。
            if (runtime.Stage != JourneyRuntimeStage.AwaitingPickupArrival)
            {
                return await RefuseInTransactionAsync(runtime, demandId, trigger, DemandReleaseReasons.AfterArrival, transaction,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!pickupOrderSettled && !NeverDispatched(await dbContext.OrderIntents.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.UpperId == pickup.UpperId, cancellationToken).ConfigureAwait(false)))
            {
                return await RefuseInTransactionAsync(runtime, demandId, trigger, DemandReleaseReasons.PickupOrderAppeared,
                    transaction, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (await PickupOrderExistsAsync(pickup, cancellationToken).ConfigureAwait(false))
        {
            return await RefuseInTransactionAsync(runtime, demandId, trigger, DemandReleaseReasons.PickupOrderAppeared,
                transaction, cancellationToken).ConfigureAwait(false);
        }

        bool last = await DemandJourneyLookup.IsLastOpenDemandAsync(dbContext, runtime.JourneyId, demandId, cancellationToken)
            .ConfigureAwait(false);
        if (last)
        {
            // 车上没有别的需求了：计划收尾（它的停靠连同当前下一站一起删），旅程按批次7-02 关闭、三套占用一起放。
            await JourneyPlanRevisionStage.StageAsync(
                dbContext, runtime.JourneyId, [demandId], currentStopMayGo: true, routing: null, cancellationToken)
                .ConfigureAwait(false);
            await new PickupStopTermination(dbContext)
                .StageJourneyClosureAsync(runtime, DemandReleaseReasons.Released, now, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await JourneyPlanRevisionStage.StageAsync(
                dbContext, runtime.JourneyId, [demandId], currentStopMayGo: false,
                await routingSource.ReadAsync(cancellationToken).ConfigureAwait(false), cancellationToken)
                .ConfigureAwait(false);
        }

        membership.RemovedAt = now;
        membership.RemovalReason = DemandReleaseReasons.Released;
        JourneyBacklogRow? backlog = await dbContext.JourneyBacklog
            .SingleOrDefaultAsync(row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
        if (backlog is not null)
        {
            // FirstSeenAt 不动：等待年龄从它算，释放不让一条需求重新排到队尾。
            backlog.AcceptedAt = null;
            backlog.ReasonCode = DemandReleaseReasons.Released;
            backlog.LastSeenAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        LogReleased(logger, demandId, runtime.JourneyId, trigger, null);
        return new DemandReleaseOutcome(runtime.JourneyId, demandId, trigger, "RELEASED");
    }

    /// <summary>这个取货停靠有没有 RIoT 订单意图：追加的停靠要到车离开上一站才建单，所以有就是「这一轮开头之后刚建的」。</summary>
    private Task<bool> PickupOrderExistsAsync(JourneyStopRow pickup, CancellationToken cancellationToken) =>
        dbContext.OrderIntents.AsNoTracking().AnyAsync(row => row.UpperId == pickup.UpperId, cancellationToken);

    private async Task<DemandReleaseOutcome> RefuseAsync(
        JourneyRuntimeRow journey,
        string demandId,
        string trigger,
        string reason,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        JourneyRuntimeRow runtime = await dbContext.JourneyRuntimes
            .SingleAsync(row => row.JourneyId == journey.JourneyId, cancellationToken).ConfigureAwait(false);
        return await RefuseInTransactionAsync(runtime, demandId, trigger, reason, transaction: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 不释放，写下原因（审查 M4 之后的规则）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>到站之后不释放不写码</b>：那是正常作业。写了会与引擎在站上每轮清码来回覆盖，阻断开始时刻每轮重置，看板显示阻断
    /// 而车在正常作业，引擎只在码为空时才写的码（预离站核验过期）还会被它遮住。
    /// </para>
    /// <para>
    /// 其余拒绝<b>只在阻断码为空、或已经是释放写下的码时写</b>：引擎的码（故障、检查点、会话）比「没能释放」更要紧，永不覆盖；
    /// 释放自己的码随原因改写。车重新合格时由 <see cref="ClearRefusalAsync"/> 清掉。
    /// Warning 只在码真的写下或改变时打，重复的拒绝降为 Debug，免得被拒期间每轮一条。
    /// </para>
    /// </remarks>
    private async Task<DemandReleaseOutcome> RefuseInTransactionAsync(
        JourneyRuntimeRow runtime,
        string demandId,
        string trigger,
        string reason,
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        bool written = reason != DemandReleaseReasons.AfterArrival &&
                       (runtime.BlockReasonCode is null || DemandReleaseReasons.IsRefusalCode(runtime.BlockReasonCode)) &&
                       !string.Equals(runtime.BlockReasonCode, reason, StringComparison.Ordinal);
        if (written)
        {
            runtime.SetBlockReason(reason, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (written)
        {
            LogRefused(logger, demandId, runtime.JourneyId, trigger, reason, null);
        }
        else
        {
            LogRefusedAgain(logger, demandId, runtime.JourneyId, trigger, reason, null);
        }
        return new DemandReleaseOutcome(runtime.JourneyId, demandId, trigger, reason);
    }

    /// <summary>旅程上挂着释放写下的拒绝码时清掉它；引擎的码不碰。</summary>
    private async Task ClearRefusalAsync(JourneyRuntimeRow journey, CancellationToken cancellationToken)
    {
        if (!DemandReleaseReasons.IsRefusalCode(journey.BlockReasonCode))
        {
            return;
        }

        dbContext.ChangeTracker.Clear();
        JourneyRuntimeRow runtime = await dbContext.JourneyRuntimes
            .SingleAsync(row => row.JourneyId == journey.JourneyId, cancellationToken).ConfigureAwait(false);
        if (runtime.BlockReasonCode is not { } code || !DemandReleaseReasons.IsRefusalCode(code))
        {
            return;
        }

        runtime.SetBlockReason(null, timeProvider.GetUtcNow());
        runtime.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogRefusalCleared(logger, runtime.JourneyId, code, null);
    }

    private async Task<RiotVehicleObservation?> ReadVehicleAsync(JourneyRuntimeRow journey, CancellationToken cancellationToken)
    {
        try
        {
            RiotVehicleObservation observation = await vehicleFacts
                .ReadVehicleAsync(journey.VehicleKey, cancellationToken).ConfigureAwait(false);
            // 网关对 SDK 失败不抛，而是返回这个形状（HttpRiotMovementGateway.UnknownVehicle）。这里把它也当作「没读到」
            // 早退，只为日志里记下一次读不到；<b>保证不在这里</b>：DemandReleaseRules.VehicleNoLongerEligible 只在观测在线时
            // 判观测派生的判据，这一行删掉或网关换了失败形状，一次失败读取照样不会成为释放理由（服务端自己的事实照常判）。
            if (observation is { Connected: false, ProcState: "UNKNOWN" } && string.IsNullOrEmpty(observation.CurrentMap))
            {
                LogVehicleUnread(logger, journey.VehicleKey, journey.JourneyId, null);
                return null;
            }

            return observation;
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            LogVehicleUnread(logger, journey.VehicleKey, journey.JourneyId, error);
            return null;
        }
    }
}

/// <summary>开往当前下一站那张取货单的了结方式。</summary>
internal enum PickupOrderSettlement
{
    /// <summary>没了结，不释放。</summary>
    None,

    /// <summary>确定从没发出过创建，没有东西可取消。</summary>
    NoOrder,

    /// <summary>取消已对账确认。</summary>
    Cancelled,

    /// <summary>订单在 RIoT 上已是取消、失败、删除或挂起的终态：没有活订单，不发取消（复审中 2）。</summary>
    Ended,
}
