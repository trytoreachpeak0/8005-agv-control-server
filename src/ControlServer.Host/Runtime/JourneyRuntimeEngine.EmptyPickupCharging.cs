using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Release;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 空车取货旅程途中电量跌破强制充电线（control-server#883，用户 2026-11-17 定方案 B）：当前这一站照常等完，之后后面还没去的取货站撤回、
/// 需求退回待派，旅程收尾，车交给现有的充电分配。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有这一条。</b>强制充电线（<c>MandatoryChargeEntryThreshold</c>，<see cref="Dispatch.Criteria.BatteryEligibility.IsMandatoryCharge"/>）
/// 原先只挡新用途：派车、途中追加、空闲返回。已经在跑的取货旅程不受它管，而挂着旅程的车不进充电分配（REQ-0290，<c>ChargingAllocator</c> 的
/// <c>CHARGING_VEHICLE_HAS_PURPOSE</c>）。11-05 夜 agv01 一趟取货旅程在没人录入的机台间轮了 1.5 小时，00:11 跌破 40% 之后一路轮到 15% 才结束，
/// 之后 RIoT 没接那张充电单，车载机低电反复断电。
/// </para>
/// <para>
/// <b>落点是离站那一刻</b>（<see cref="JourneyRuntimeStage.AwaitingStationDeparture"/>，离站等待已过、持货等单与仓门扣车之后、发离站核验之前）。
/// 方案 B 要「当前这一站照常等完」：有人扫码照常装，没人扫码就按站点期限结束（REQ-0365，那条需求终结、按业务键抑制、挂人工搬运告警——与电量无关，
/// 本规则不改它）。一站的每一种结束（期限、装完、扫码前取消、同站追加提前结束）最后都走到离站等待这一段，所以在这里判一次就盖住了全部；车途中
/// 跌破线时也一样，它照常开到正前往的那一站、等完，再在这里判。离站核验一旦发出，下一段的单紧跟着就建，本规则不在那之后插手。
/// </para>
/// <para>
/// <b>只管空车。</b>整趟旅程上只要有一条已装上或正在装（<see cref="JourneyDemandStatuses.Loaded"/>、<see cref="JourneyDemandStatuses.Loading"/>），
/// 什么也不做：车上的货要有需求认领，那是带货的既有规则（control-server#782 的充电停靠插入、#866 的检查点等人告警）。当前这一站有人扫码装上了货，
/// 车就不再是空车，也归它们。
/// </para>
/// <para>
/// <b>放回与待送点缺电（control-server#709）同一个做法</b>：<see cref="DemandReleaseService.StageReleasedMembershipAsync"/>、默认原因
/// <see cref="DemandJourneyLookup.ReleasedForRedispatchReason"/>——需求仍是 <c>Accepted</c>，<b>不终结、不写按业务键的抑制</b>
/// （<see cref="PickupStopTermination.KeySuppressingReasonCodes"/> 只在终结时查，这里不经终结），等待年龄不重置，不计 control-server#703 的站点超时
/// 放回次数，不挂告警；下一轮派车按「已释放待改派」以新的一代重新受理。不走人工撤下（<see cref="Withdrawal.JourneyWithdrawal"/>）：那是要现场核实、
/// 确认码与审计的人工操作，这里是服务端自己的判断，要的只是它收尾的那一半，而那一半本来就是共用的这两步。
/// </para>
/// <para>
/// <b>收尾</b>与站点期限放回最后一条那一支相同（<see cref="PickupStopTermination.StageJourneyClosureAsync(JourneyRuntimeRow, string?, string, DateTimeOffset, CancellationToken)"/>）：
/// 车就停在这一站，不修订计划；租约、订单占用与用途声明一起放，下一轮充电分配看到的是一辆空闲车。后面那几站的单还没建（下一段的单在离站时才建），
/// 所以没有 RIoT 订单要取消。收尾原因 <see cref="EmptyPickupChargingRequiredReason"/>，车上说 <c>DEMAND_RELEASED</c>。
/// </para>
/// <para>
/// <b>与待送点缺电同样的几道闸</b>：读不到电量、读数过期、没有冻结的策略版本都不动（<see cref="MandatoryChargeBatteryAsync"/>）；仓门没证明关着不放
/// （放了车就会被派去充电）；车不在线不放；写锁里重读阶段与游标，期间变了就不动。
/// </para>
/// </remarks>
public sealed partial class JourneyRuntimeEngine
{
    private static readonly Action<ILogger, string, int, int, string, Exception?> LogEmptyPickupBelowLineReleasedDemand =
        LoggerMessage.Define<string, int, int, string>(
            LogLevel.Warning,
            new EventId(2347, nameof(LogEmptyPickupBelowLineReleasedDemand)),
            "Vehicle {AgvId} leaves a pickup stop with nothing loaded and its battery at {BatteryPercent}% is below the " +
            "mandatory charge line of {EntryThreshold}%; demand {DemandId} went back to the backlog instead of being visited, " +
            "and the journey ended so charging allocation can send the vehicle to charge.");

    /// <summary>见类型注释。做了（旅程已收尾）返回真；返回假时调用方照常离站。</summary>
    private async Task<bool> TryReleaseEmptyPickupJourneyBelowChargeLineAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!NothingOnBoardAndOnlyPickupsAhead(stops))
        {
            return false;
        }
        if (await MandatoryChargeBatteryAsync(runtime, cancellationToken).ConfigureAwait(false) is not { } low)
        {
            return false;
        }
        if (SlotDoorsNotProvenClosed(session))
        {
            return false;
        }
        DateTimeOffset? lastInboundAt = await LatestInboundAtForSessionAsync(
            runtime.AgvId, session.SessionGeneration, now - runtimeOptions.MaximumEvidenceAge, cancellationToken)
            .ConfigureAwait(false);
        if (lastInboundAt is null || now - lastInboundAt.Value > runtimeOptions.MaximumEvidenceAge)
        {
            return false;
        }

        await using IDbContextTransaction? transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        JourneyRuntimeStage? stageNow = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.JourneyId == runtime.JourneyId)
            .Select(row => (JourneyRuntimeStage?)row.Stage)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (stageNow != JourneyRuntimeStage.AwaitingStationDeparture)
        {
            return false;
        }
        // 锁里重新加载：锁外那次读之后，别的连接可能刚受理了一次录入结果、或终结了其中一条。
        JourneyStopCursor locked = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
        if (!NothingOnBoardAndOnlyPickupsAhead(locked))
        {
            return false;
        }

        string? sublotRequestMessageId = locked.CurrentSublotRequestMessageIdOrNone(runtime.WorklistRevision);
        string currentStopId = locked.Current.StopId;
        string[] leaving = [.. locked.Demands.Select(item => item.Demand.DemandId)];
        int releasedFromCurrentStop = locked.Demands.Count(item =>
            string.Equals(item.Membership.PickupStopId, currentStopId, StringComparison.Ordinal));
        foreach (string demandId in leaving)
        {
            JourneyDemandRow membership = await dbContext.Set<JourneyDemandRow>()
                .SingleAsync(
                    row => row.JourneyId == runtime.JourneyId && row.DemandId == demandId && row.RemovedAt == null,
                    cancellationToken)
                .ConfigureAwait(false);
            await DemandReleaseService.StageReleasedMembershipAsync(dbContext, membership, now, cancellationToken)
                .ConfigureAwait(false);
        }
        // 版数守恒，与站点期限放回同一个理由（今天走到这里时本站的待做项都已结束，这个数恒为 0；照算，不靠这个前提）。
        if (releasedFromCurrentStop > 0)
        {
            (await TrackedStopAsync(currentStopId, cancellationToken).ConfigureAwait(false)).WorklistRefills +=
                releasedFromCurrentStop;
        }
        await new PickupStopTermination(dbContext).StageJourneyClosureAsync(
            runtime, sublotRequestMessageId, EmptyPickupChargingRequiredReason, now, cancellationToken)
            .ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        await JourneyClosure.SendAsync(publisher, dbContext, runtime.AgvId, cancellationToken).ConfigureAwait(false);
        checkpointWaits.Clear(runtime.VehicleKey);
        foreach (string demandId in leaving)
        {
            LogEmptyPickupBelowLineReleasedDemand(
                logger, runtime.AgvId, low.Percent, low.EntryThreshold, demandId, null);
        }
        return true;
    }

    /// <summary>
    /// 车上什么都没有（整趟旅程没有一条已装上或正在装），而旅程还带着需求、每一条都是还没装的（<c>Accepted</c>、<see cref="JourneyDemandStatuses.PendingLoad"/>）。
    /// </summary>
    private static bool NothingOnBoardAndOnlyPickupsAhead(JourneyStopCursor stops) =>
        stops.Demands.Count > 0 &&
        !stops.AllDemands.Any(item =>
            item.Membership.Status is JourneyDemandStatuses.Loading or JourneyDemandStatuses.Loaded) &&
        stops.Demands.All(item =>
            item.Demand.Status == DemandExecutionStatus.Accepted &&
            item.Membership.Status == JourneyDemandStatuses.PendingLoad);
}
