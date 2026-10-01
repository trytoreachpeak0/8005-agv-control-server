using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

// 批次9-06（control-server#404）：充电旅程从承诺到去桩途中的推进。承诺（周期、CHARGING 用途占有、CHARGER 预占、旅程行、停靠与订单意图，
// 同一次保存）由派车轮里的充电分配形成（ChargingAllocator）；这里发计划与业务状态、过出发前安全门、建单与对账、途中监看、失败收尾。
// 到桩、充电中、充满在 JourneyRuntimeEngine.ChargingCycle.cs，离桩与释放在充电分配的清扫里（批次9-07）。
public sealed partial class JourneyRuntimeEngine
{
    private static readonly Action<ILogger, string, string, string, Exception?> LogChargingDepartureNotProven =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(2250, nameof(LogChargingDepartureNotProven)),
            "Charging journey {JourneyId} of vehicle {VehicleKey} is not sent off: the pre-departure safety gate is not met " +
            "({Gaps}). No order is created; the commitment, the charger reservation and the cycle stay, and the gate is asked " +
            "again next round.");

    private static readonly Action<ILogger, string, string, string, Exception?> LogChargingHeld =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2251, nameof(LogChargingHeld)),
            "{ReasonCode}: charging journey {JourneyId} of vehicle {VehicleKey} is held as it is; no order is created and nothing " +
            "is released. Someone has to look.");

    private static readonly Action<ILogger, string, string, string, string, int, Exception?> LogChargingWithdrawn =
        LoggerMessage.Define<string, string, string, string, int>(
            LogLevel.Warning,
            new EventId(2254, nameof(LogChargingWithdrawn)),
            "{ReasonCode}: the charging commitment {JourneyId} of vehicle {VehicleKey} is withdrawn before any order was " +
            "created ({Detail}). The cycle is closed and the CHARGING purpose and the reservation of charger {StationId} are " +
            "released in the same save; the vehicle is judged afresh next round.");

    private static readonly Action<ILogger, string, string, string, TimeSpan, int, Exception?> LogChargingOrderAbandoned =
        LoggerMessage.Define<string, string, string, TimeSpan, int>(
            LogLevel.Warning,
            new EventId(2255, nameof(LogChargingOrderAbandoned)),
            "CHARGING_ORDER_NEVER_APPEARED: the create of charge order {UpperId} (journey {JourneyId}, vehicle {VehicleKey}) " +
            "went out and its result was never learned; RIoT has answered 'no such order' for {Absent} without a break, the " +
            "vehicle is proven stopped and has no unfinished order. The order is given up and the charging cycle to charger " +
            "{StationId} ends as a confirmed failure. Should RIoT show that order running on the vehicle after all, it is " +
            "cancelled and alarmed (event 2180, basis OWN_CHARGE_ORDER_ABANDONED).");

    private static readonly Action<ILogger, string, string, int, DateTimeOffset, Exception?> LogChargingEndNotProven =
        LoggerMessage.Define<string, string, int, DateTimeOffset>(
            LogLevel.Warning,
            new EventId(2256, nameof(LogChargingEndNotProven)),
            "Charging journey {JourneyId}: its order was cancelled or deleted in RIoT, and vehicle {VehicleKey} has not been " +
            "proven stopped without an order since {Since} (it cannot be read, is offline, or reads moving). The CHARGING " +
            "purpose and the reservation of charger {StationId} are kept until it is, so no other vehicle can be allocated " +
            "that charger. Someone has to look at the vehicle (the manual release of a charger is control-server#406).");

    private static readonly Action<ILogger, string, string, string, int, Exception?> LogChargingEnRoute =
        LoggerMessage.Define<string, string, string, int>(
            LogLevel.Information,
            new EventId(2252, nameof(LogChargingEnRoute)),
            "Charging journey {JourneyId}: RIoT confirmed order {UpperId}; vehicle {VehicleKey} is en route to charger {StationId}.");

    private static readonly Action<ILogger, string, string, string, int, Exception?> LogChargingEnded =
        LoggerMessage.Define<string, string, string, int>(
            LogLevel.Warning,
            new EventId(2253, nameof(LogChargingEnded)),
            "Charging journey {JourneyId} of vehicle {VehicleKey} ended with {ReasonCode} before reaching charger {StationId}: " +
            "the cycle is closed and the CHARGING purpose released; nothing is rebuilt. The charger reservation is released " +
            "only once charging has stopped, the vehicle is off the charger and the charger is confirmed free.");

    /// <summary>
    /// 充电旅程的推进：不进搬运状态机（没有清单、录入、装卸），只走这一段。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>线上的两步</b>（票面第 6、7 条）：承诺之后先发 <c>ALLOCATED</c>（计划里一条 <c>PLANNED</c> 的 <c>CHARGER</c> 腿）；RIoT 确认建单之后
    /// 周期才进 <c>EN_ROUTE</c>、才发 <c>EN_ROUTE</c>（腿 <c>ACTIVE</c>）。建单结果未知时周期停在 <c>ALLOCATED</c>，线上不报 <c>EN_ROUTE</c>。
    /// </para>
    /// <para>
    /// <b>出发前安全门</b>：去充电是非业务移动，协议 <c>2.0.0</c> 的 <c>PreDepartureSafetyCheck</c> 必须带需求与移动段，发不出，也不许编一个 id 去填。
    /// 所以单从没发出过时，建单之前由服务端自己核同一组事实——与空闲返回同一道门（<see cref="IdleReturnDepartureGapsAsync"/>：会话就绪且听得到、
    /// 车载端安全摘要说可以出发、八个仓位全部锁闭且开锁输出复位、没有阻断事实、没有故障、在本图、RIoT 报停止、充电策略仍然可用）。
    /// 没过就不建单、写 <see cref="ChargingExecutionReasons.DepartureNotProven"/>，承诺、预占与周期保持，下一轮再问。
    /// </para>
    /// <para>
    /// <b>出发前复核，不成立就撤回承诺</b>（独立审查 M1、M2(a)；做法对照空闲返回建单前的重新核验）。承诺与出发之间可以隔很久，所以单从没发出过时，
    /// 每一轮建单之前先核：预占还是这一趟的；这一个桩按分配时的同一条候选链仍然成立（在当前生效名册里——名册被置空即不成立，窗口外不自动充电——、
    /// 没有分配暂停、在当前地图上且站名一致、占用事实可确认空闲、路线可达，<c>ChargingAllocator.WhyCommittedChargerNoLongerStandsAsync</c>）；
    /// 这辆车按它冻结的那一版策略仍低于强制充电线。任何一条不成立，同一次保存里周期结束、<c>CHARGING</c> 用途与桩预占释放、旅程收尾，
    /// 车下一轮按正常链重评。安全门持续不过超过 <c>JourneyRuntime:OwnOrderRebuildDelay</c> 同样撤回——不让一辆出不了门的车一直占着桩；
    /// 宽限是为了不让门刚开一下就来回抖动。撤回不是失败：什么也没发出过，不计入「两次即停」，不进冷却。
    /// 单已经发出、车已在路上的不走这一段，按自己记下的名册版本继续（票面 8.2）。
    /// </para>
    /// <para>
    /// <b>结果未知时五样全保持</b>（<c>REQ-0283</c>、<c>REQ-0173</c>）：车辆、充电意图、目标桩、预占、周期都不动；单号不换（意图按
    /// <c>upperId</c> 复用，<see cref="MovementDispatchService.ReconcileOrCreateAsync"/> 对发过的单只对账不再建）；不换桩、不释放。
    /// 电量继续下降不改变任何一样，只由等人告警升级（<c>REQ-0169</c>，<c>WaitingJourneyWatch</c>）。
    /// </para>
    /// <para>
    /// <b>结果未知的出口</b>（独立审查 M2(b)）：保持不能是永久的——建单应答丢了而 RIoT 上根本没有这张单时，承诺会永远占着用途与桩，只能改库解开。
    /// 全部成立才放弃这张单、按已确认失败收尾（<see cref="ChargingExecutionReasons.OrderNeverAppeared"/>，计入「两次即停」，走与取消相同的冷却，
    /// 桩预占照旧按三项确认释放）：RIoT 对这个 <c>upperId</c> 明确答「查无此单」（HTTP 404，或真实 RIoT 的答法：HTTP 200、业务码 0、不带 result，且没有失败类别，
    /// 即 <see cref="RiotOrderObservation.IsExactAbsentAtObservation"/>；增量审查 M-A），连续满 <c>JourneyRuntime:ChargingOrderAbsentAbandonAfter</c>
    /// （中间任何一次读到别的、或读不到，重新计时）；车证明停稳、没有任务号；RIoT 的未完成订单清单读全了、里面没有这辆车的单。
    /// 那张单事后才在 RIoT 冒出来、跑到这辆车上时，由外来单监督器取消并告警（<see cref="AbandonedChargeOrders"/>）。
    /// </para>
    /// <para>
    /// <b>途中监看与搬运同一套</b>（<see cref="NameStalledOrderAsync"/>，票面第 11 条）：单 FAILED 交故障模型（疑似故障、按住、必要时急停）；
    /// 途中仓门没有证明锁闭交故障模型；急停、切手动让 RIoT 把单挂起（<c>HANG</c>）写 <c>ORDER_HANG</c>——那不是「充不上」（<c>REQ-0175</c>），
    /// 不暂停桩、不暂停车的充电资格、不释放、不重建，RIoT 继续之后原因码清掉。不看车载端会话就走：这些证据都是 RIoT 的。
    /// </para>
    /// <para>
    /// <b>充电单被取消或删除：不重建</b>（票面第 10 条）。与空闲返回单同一条路：<c>REQ-0360</c> 的重建继续承载原订单未终止的需求，充电单没有需求；
    /// 而车已到桩、单进 <c>HANG</c> 之后的取消是旧单的预期终结（<c>REQ-0178</c>），重建等于向原桩再发一次充电动作（<c>REQ-0284</c> 原桩重试为 0）。
    /// 不登记重建的那道判别在 <see cref="RecordOrderEndedInRiotAsync"/>。车还可能在动时保持一切、写
    /// <see cref="OrderEndedWithoutArrivalReason"/>；车证明停稳、没有活动订单之后按已确认失败收尾（<see cref="ChargingEnding"/>）。
    /// <b>收尾不放桩预占</b>：那要三项确认，由充电分配每轮开头的清扫做。
    /// </para>
    /// <para>
    /// <b>单到了终态 <c>SUCCESS</c></b>：充电动作已经接上，交给到桩之后那一段（批次9-07，<see cref="AdvanceAtChargerAsync"/>）：到桩、开始充电、充满。
    /// </para>
    /// <para>
    /// <b>车可能在动时不按超时释放。</b>单发出过之后，这里没有一个只因为等久了就放车、放桩或换单号的分支：放弃一张查无此单的充电单要凭
    /// 上面那几项证据，取消之后的收尾要凭车证明停稳。证据一直拿不到（车被关机、拖走）时保持，超过
    /// <c>JourneyRuntime:OwnOrderRebuildRepeatWindow</c> 告警一次（事件 2256）；人工清桩的出口归 control-server#406。
    /// </para>
    /// </remarks>
    private async Task AdvanceChargingAsync(
        JourneyRuntimeRow runtime,
        RiotMapStationCatalogSnapshot currentMap,
        CancellationToken cancellationToken)
    {
        if (runtime.Stage != JourneyRuntimeStage.AwaitingPickupArrival)
        {
            // Like an idle return, a charging journey is never blocked by this engine: it stays in its travelling stage with a
            // code. Any other stage was written by something that does not know charging journeys.
            throw new InvalidDataException(
                $"Charging journey {runtime.JourneyId} is in stage {runtime.Stage}; only {JourneyRuntimeStage.AwaitingPickupArrival} is advanced.");
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        JourneyStopRow stop = await dbContext.Set<JourneyStopRow>()
            .SingleAsync(row => row.JourneyId == runtime.JourneyId, cancellationToken).ConfigureAwait(false);
        OrderIntentRow intent = await dbContext.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.UpperId == stop.UpperId, cancellationToken).ConfigureAwait(false);
        ChargingCycleRow? cycle = await dbContext.Set<ChargingCycleRow>()
            .SingleOrDefaultAsync(
                row => row.JourneyId == runtime.JourneyId && row.Phase != ChargingCyclePhases.Ended, cancellationToken)
            .ConfigureAwait(false);
        if (cycle is null)
        {
            // Batch 9-08: a cycle a manual station clearance ended leaves its journey for this round to close.
            if (await CloseClearedChargingAsync(runtime, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
            await HoldChargingAsync(runtime, ChargingExecutionReasons.CycleMissing, now, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (cycle.Phase == ChargingCyclePhases.Clearing)
        {
            // Batch 9-08 (control-server#406): could not charge. The vehicle stays where it is until a person confirms the
            // charger clear; nothing below runs for it -- no order, no command, no rebuild.
            await AdvanceClearingAsync(runtime, stop, cycle, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ReplayChargingSnapshotsAsync(runtime, now, cancellationToken).ConfigureAwait(false);
        // Staged under ids derived from the journey, so every round after the first adds nothing: this is also what stages them
        // for a vehicle that had no session when the commitment was made, or after a crash between a save and its snapshots.
        await PublishChargingSnapshotsAsync(
                runtime, stop, enRoute: cycle.WireState != ChargingCycleWireStates.Allocated, cancellationToken)
            .ConfigureAwait(false);

        bool neverSent = await store.IsNeverSentAsync(intent, cancellationToken).ConfigureAwait(false);
        if (neverSent)
        {
            // Nothing has gone out yet, so nothing is owed to a commitment that no longer stands: judged again before the
            // create, every round it waits (independent review M1).
            if (await WithdrawChargingIfItNoLongerStandsAsync(runtime, stop, currentMap, now, cancellationToken)
                    .ConfigureAwait(false))
            {
                return;
            }

            IReadOnlyList<string> gaps = await IdleReturnDepartureGapsAsync(runtime, cancellationToken).ConfigureAwait(false);
            if (gaps.Count > 0)
            {
                if (!string.Equals(runtime.BlockReasonCode, ChargingExecutionReasons.DepartureNotProven, StringComparison.Ordinal))
                {
                    LogChargingDepartureNotProven(logger, runtime.JourneyId, runtime.VehicleKey, string.Join(", ", gaps), null);
                    runtime.SetBlockReason(ChargingExecutionReasons.DepartureNotProven, now);
                    runtime.UpdatedAt = now;
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (runtime.BlockReasonSince is { } since && now - since >= runtimeOptions.OwnOrderRebuildDelay)
                {
                    // Independent review M2(a): a vehicle that cannot leave does not go on holding the charger. The grace
                    // keeps a door opened for a moment from withdrawing and recommitting every round.
                    await WithdrawChargingAsync(
                            runtime, stop, ChargingExecutionReasons.WithdrawnDepartureNotProven, string.Join(", ", gaps), now,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                return;
            }
        }

        if (intent is not { Status: "CONFIRMED", OrderId: not null })
        {
            MovementDispatchResult result = await movementDispatch.ReconcileOrCreateAsync(stop.UpperId, cancellationToken)
                .ConfigureAwait(false);
            if (result.Outcome is not (MovementDispatchOutcome.Confirmed or MovementDispatchOutcome.TerminalReconciliationRequired))
            {
                // Result unknown, create gate closed, and the like: everything stays, the same upperId is reconciled next
                // round, and the cycle stays ALLOCATED.
                string waiting = ChargingExecutionReasons.LegOutcomeCode(result.Outcome);
                if (!string.Equals(runtime.BlockReasonCode, waiting, StringComparison.Ordinal))
                {
                    DateTimeOffset at = timeProvider.GetUtcNow();
                    runtime.SetBlockReason(waiting, at);
                    runtime.UpdatedAt = at;
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                // The governed way out of "result unknown" (independent review M2(b)): only for a create that did go out.
                if (result.Outcome == MovementDispatchOutcome.ResultUnknown &&
                    !await store.IsNeverSentAsync(
                            await dbContext.OrderIntents.AsNoTracking()
                                .SingleAsync(row => row.UpperId == stop.UpperId, cancellationToken).ConfigureAwait(false),
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    await AbandonChargeOrderIfItNeverAppearedAsync(runtime, stop, cancellationToken).ConfigureAwait(false);
                }
                return;
            }
            dispatchRound.Charging.Board.SeenOrUnread(stop.UpperId);
            // TerminalReconciliationRequired: the order ended before its create was confirmed. Judged below exactly like a
            // confirmed order that ended (control-server#367's reading).
            intent = await dbContext.OrderIntents.AsNoTracking()
                .SingleAsync(row => row.UpperId == stop.UpperId, cancellationToken).ConfigureAwait(false);
        }

        if (intent is { Status: "CONFIRMED", OrderId: not null } && cycle.WireState == ChargingCycleWireStates.Allocated)
        {
            // RIoT has the order: only now is the cycle en route, in one save with the code that said it was not. Also reached
            // by a round that finds the intent confirmed and the cycle still ALLOCATED -- a crash between the two.
            DateTimeOffset confirmedAt = timeProvider.GetUtcNow();
            cycle.WireState = ChargingCycleWireStates.EnRoute;
            cycle.UpperId = stop.UpperId;
            cycle.OrderConfirmedAt = confirmedAt;
            cycle.Version++;
            runtime.SetBlockReason(null, confirmedAt);
            runtime.UpdatedAt = confirmedAt;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            LogChargingEnRoute(logger, runtime.JourneyId, stop.UpperId, runtime.VehicleKey, stop.StationRiotId, null);
            await PublishChargingSnapshotsAsync(runtime, stop, enRoute: true, cancellationToken).ConfigureAwait(false);
        }

        if (cycle.ArrivedAt is not null)
        {
            // Independent review M1: the arrival is proven (an order that succeeded plus the vehicle standing on the charger),
            // and from here on the cycle is judged on the vehicle and its battery alone. The order is not read again: deleted,
            // cleaned up or answered absent afterwards, it says nothing more about a vehicle that is on the charger, and
            // waiting to read SUCCESS again would leave the vehicle on the charger for good.
            await ClearCodesNotTheChargersAsync(runtime, now, cancellationToken).ConfigureAwait(false);
            await AdvanceAtChargerAsync(runtime, stop, intent, order: null, cycle, cancellationToken).ConfigureAwait(false);
            return;
        }

        RiotOrderObservation order = await vehicleFacts.ReconcileByUpperIdAsync(stop.UpperId, cancellationToken)
            .ConfigureAwait(false);
        if (order is { Kind: RiotOrderObservationKind.Terminal, OrderState: RiotOrderState.Success })
        {
            // The charge action took: arrival, charging and completion (batch 9-07, JourneyRuntimeEngine.ChargingCycle.cs). A
            // code a stalled order left is not true any more; the codes that part writes are its own to clear.
            await ClearCodesNotTheChargersAsync(runtime, now, cancellationToken).ConfigureAwait(false);
            await AdvanceAtChargerAsync(runtime, stop, intent, order, cycle, cancellationToken).ConfigureAwait(false);
            return;
        }

        bool absent = order.Kind == RiotOrderObservationKind.NotFound || order.IsExactAbsentAtObservation(stop.UpperId);
        if (absent || order is { Kind: RiotOrderObservationKind.Terminal, OrderState: RiotOrderState.Cancelled or RiotOrderState.Deleted })
        {
            // Independent review M1: gone before its success was seen. A vehicle standing still on this charger and reading
            // CHARGING is there and charging whatever became of the order: taken as arrived with the order lost, and judged on
            // its battery from here on. Anything less is not taken as an arrival.
            if (await ArriveWithTheOrderLostAsync(runtime, stop, intent, cycle, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
            if (absent)
            {
                // Not an ending RIoT reported, so nothing below judges it: named, kept, and told once past the window.
                // Never advanced by a timeout -- the vehicle may still be on its way.
                await NameChargingLossAsync(runtime, stop, ChargingExecutionReasons.OrderNotFound, now, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }
        else if (runtime.BlockReasonCode == ChargingExecutionReasons.OrderNotFound)
        {
            // Read again: the code is no longer true.
            dispatchRound.Charging.Board.Unsay(
                ChargingAllocationBoard.ChargingLossKey(runtime.JourneyId, ChargingExecutionReasons.OrderNotFound));
            runtime.SetBlockReason(null, now);
            runtime.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // Batch 9-08 (control-server#406): a HANG with every strict fact of REQ-0174 is a confirmed failure to charge. Anything
        // less -- a HANG alone, a HANG with another code -- goes on below as the HANG it is (REQ-0175).
        if (order is { Kind: RiotOrderObservationKind.Active, OrderState: RiotOrderState.Hang })
        {
            if (await ConfirmUnableToChargeAsync(runtime, stop, intent, order, cycle, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
        else
        {
            dispatchRound.Charging.Board.BreakSampleRun(UnableToChargeFacts.ContinuityKey(cycle.CycleId));
            dispatchRound.Charging.Board.ForgetObserved(UnableToChargeFacts.ContinuityKey(cycle.CycleId));
        }

        if (await NameStalledOrderAsync(runtime, intent, order, cancellationToken).ConfigureAwait(false))
        {
            if (order is { Kind: RiotOrderObservationKind.Terminal, OrderState: RiotOrderState.Cancelled or RiotOrderState.Deleted })
            {
                await EndChargingOnceStoppedAsync(runtime, stop, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        await NameCheckpointWaitAsync(runtime, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 充电单已被取消或删除：车证明停稳、没有活动订单，才按已确认失败收尾；否则什么也不动（旅程上已经是
    /// <see cref="OrderEndedWithoutArrivalReason"/>）。
    /// </summary>
    /// <remarks>
    /// 「证明停稳」与空闲返回同一组事实：RIoT 车辆读数在线、空闲、速度为零、无在执行任务、新鲜，并且 RIoT 的运动安全读数为停止。任一不满足、
    /// 或读不到，都不收尾——「取消即释放而车其实还在动」是这一段要堵的事。
    /// </remarks>
    private async Task EndChargingOnceStoppedAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        CancellationToken cancellationToken)
    {
        string notProvenKey = ChargingAllocationBoard.EndNotProvenKey(runtime.JourneyId);
        if (await ProvenStoppedWithoutOrderAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false)
            is not { } readAt)
        {
            // Independent review M2(c): a vehicle switched off or towed away after the cancellation never gives the proof, and
            // the purpose and the charger stay taken. Past the repeat window somebody is told, once.
            if (runtime.BlockReasonSince is { } since &&
                timeProvider.GetUtcNow() - since > runtimeOptions.OwnOrderRebuildRepeatWindow &&
                dispatchRound.Charging.Board.FirstTime(notProvenKey))
            {
                LogChargingEndNotProven(logger, runtime.JourneyId, runtime.VehicleKey, stop.StationRiotId, since, null);
            }
            return;
        }

        dispatchRound.Charging.Board.Unsay(notProvenKey);
        checkpointWaits.Clear(runtime.VehicleKey);
        await ChargingEnding.StageAsync(dbContext, runtime, stop, ChargingExecutionReasons.OrderEnded, readAt, cancellationToken)
            .ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogChargingEnded(
            logger, runtime.JourneyId, runtime.VehicleKey, ChargingExecutionReasons.OrderEnded, stop.StationRiotId, null);
        await JourneyClosure.SendAsync(publisher, dbContext, runtime.AgvId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 这辆车此刻证明停稳、没有活动订单：RIoT 车辆读数在线、空闲、速度为零、无任务号、新鲜，并且运动安全读数为停止。成立答读数的时刻，
    /// 任一不满足或读不到答空。
    /// </summary>
    private async Task<DateTimeOffset?> ProvenStoppedWithoutOrderAsync(string vehicleKey, CancellationToken cancellationToken)
    {
        RiotVehicleObservation vehicle;
        RiotVehicleSafetyObservation safety;
        try
        {
            vehicle = await vehicleFacts.ReadVehicleAsync(vehicleKey, cancellationToken).ConfigureAwait(false);
            safety = await vehicleSafety.ReadVehicleSafetyAsync(vehicleKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        DateTimeOffset readAt = timeProvider.GetUtcNow();
        return vehicle.Connected && vehicle.ProcState == "IDLE" && vehicle.Speed == 0 &&
               string.IsNullOrWhiteSpace(vehicle.OrderTaskId) &&
               vehicle.ObservedAt <= readAt && readAt - vehicle.ObservedAt <= runtimeOptions.MaximumEvidenceAge &&
               safety.MotionState == RiotVehicleMotionState.Stopped
            ? readAt
            : null;
    }

    /// <summary>
    /// 出发前复核（独立审查 M1）：预占、这一个桩、还需不需要充电。任何一条不成立就撤回承诺并答真。
    /// </summary>
    private async Task<bool> WithdrawChargingIfItNoLongerStandsAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        RiotMapStationCatalogSnapshot currentMap,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The reservation is this journey's, or the commitment is void: a charger it does not hold may be anyone's.
        if (!await dbContext.Set<StationExclusivityRow>().AsNoTracking()
                .AnyAsync(
                    row => row.MapId == runtime.MapId && row.StationId == stop.StationRiotId &&
                           row.StationKind == StationExclusivityKinds.Charger && row.JourneyId == runtime.JourneyId,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            await WithdrawChargingAsync(
                    runtime, stop, ChargingExecutionReasons.WithdrawnReservationLost, "the reservation row is gone", now,
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        string? charger;
        RiotVehicleObservation? vehicle;
        try
        {
            charger = await dispatchRound.Charging
                .WhyCommittedChargerNoLongerStandsAsync(runtime.VehicleKey, runtime.MapId, stop.StationRiotId, currentMap, cancellationToken)
                .ConfigureAwait(false);
            vehicle = charger is null
                ? await vehicleFacts.ReadVehicleAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            // RIoT could not be read: nothing about the charger is confirmed. Not knowing is not a reason to set off.
            charger = $"RIOT_UNREADABLE ({error.GetType().Name})";
            vehicle = null;
        }

        if (charger is not null)
        {
            await WithdrawChargingAsync(
                    runtime, stop, ChargingExecutionReasons.WithdrawnChargerNoLongerEligible, charger, now, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        // Still below the line of the policy version this journey froze (REQ-0282)? A vehicle somebody has charged by hand
        // meanwhile, or whose battery cannot be read, is not sent. A policy that is missing or broken is the departure gate's
        // to name (it judges commissioning), so those two answers fall through to it.
        string battery = runtime.ChargingPolicyVersion is long version
            ? Dispatch.Criteria.BatteryEligibility.Judge(
                vehicle!,
                Dispatch.Criteria.DispatchBatteryPolicy.From(
                    await chargingPolicy.ReadFrozenAsync(version, cancellationToken).ConfigureAwait(false)),
                tasksToCover: 1,
                runtimeOptions.WaitingJourneyRescueBatteryPercent)
            : Dispatch.DispatchReasonCodes.ChargingPolicyNotApproved;
        if (battery is Dispatch.DispatchReasonCodes.MandatoryChargeRequired
            or Dispatch.DispatchReasonCodes.ChargingPolicyNotApproved
            or Dispatch.DispatchReasonCodes.ChargingPolicyEntryNotAboveRescueLine)
        {
            return false;
        }

        await WithdrawChargingAsync(
                runtime, stop, ChargingExecutionReasons.WithdrawnNoLongerRequired,
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{battery}; battery {vehicle!.BatteryPercent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unread"}, state {vehicle.BatteryState ?? "unread"}"),
                now, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 撤回一个单从没发出过的充电承诺：周期结束、<c>CHARGING</c> 用途释放、桩预占释放、旅程收尾，同一次保存；然后告诉车。
    /// </summary>
    /// <remarks>
    /// 桩预占在这里当场放，不等三项确认：那三项是为「车可能停在桩上、可能还在充」设的，而这辆车从没被派出过。
    /// </remarks>
    private async Task WithdrawChargingAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        string reasonCode,
        string detail,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        checkpointWaits.Clear(runtime.VehicleKey);
        await ChargingEnding.StageAsync(dbContext, runtime, stop, reasonCode, now, cancellationToken).ConfigureAwait(false);
        StationExclusivityRow? held = await dbContext.Set<StationExclusivityRow>()
            .SingleOrDefaultAsync(
                row => row.MapId == runtime.MapId && row.StationId == stop.StationRiotId &&
                       row.StationKind == StationExclusivityKinds.Charger && row.JourneyId == runtime.JourneyId,
                cancellationToken)
            .ConfigureAwait(false);
        if (held is not null)
        {
            await FixedStationExclusivity.StageReleaseAsync(dbContext, held, now, reasonCode, cancellationToken)
                .ConfigureAwait(false);
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogChargingWithdrawn(logger, reasonCode, runtime.JourneyId, runtime.VehicleKey, detail, stop.StationRiotId, null);
        await JourneyClosure.SendAsync(publisher, dbContext, runtime.AgvId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 建单发出过、结果未知：RIoT 对这张单连续答「查无此单」够久、车证明停稳、名下没有未完成的单，就放弃这张单、按已确认失败收尾
    /// （独立审查 M2(b)）。缺任何一项这一轮什么也不做。
    /// </summary>
    private async Task AbandonChargeOrderIfItNeverAppearedAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        CancellationToken cancellationToken)
    {
        ChargingAllocationBoard board = dispatchRound.Charging.Board;
        RiotOrderObservation observed;
        try
        {
            observed = await vehicleFacts.ReconcileByUpperIdAsync(stop.UpperId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            board.SeenOrUnread(stop.UpperId);
            return;
        }

        // Only a "no such order" counts: HTTP 404, or the exact absent-at-observation read -- HTTP 200 / code 0 with no result,
        // which is what real RIoT answers (review M-A; the create path reads it the same way). An answer that could not be
        // classified, one with a failure category or a code, or an order that is there, breaks the run and the count starts again.
        if (observed.Kind != RiotOrderObservationKind.NotFound && !observed.IsExactAbsentAtObservation(stop.UpperId))
        {
            board.SeenOrUnread(stop.UpperId);
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        TimeSpan absent = now - board.AbsentSince(stop.UpperId, now);
        if (absent < runtimeOptions.ChargingOrderAbsentAbandonAfter)
        {
            return;
        }

        if (await ProvenStoppedWithoutOrderAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false) is not { } readAt)
        {
            return;
        }

        bool noUnfinishedOrder;
        try
        {
            noUnfinishedOrder = await dispatchRound.Charging.Occupancy
                .VehicleHasNoUnfinishedOrderAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            return;
        }
        if (!noUnfinishedOrder)
        {
            return;
        }

        checkpointWaits.Clear(runtime.VehicleKey);
        await ChargingEnding
            .StageAsync(dbContext, runtime, stop, ChargingExecutionReasons.OrderNeverAppeared, readAt, cancellationToken)
            .ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        board.SeenOrUnread(stop.UpperId);
        LogChargingOrderAbandoned(logger, stop.UpperId, runtime.JourneyId, runtime.VehicleKey, absent, stop.StationRiotId, null);
        await JourneyClosure.SendAsync(publisher, dbContext, runtime.AgvId, cancellationToken).ConfigureAwait(false);
    }

    private async Task HoldChargingAsync(
        JourneyRuntimeRow runtime,
        string code,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.Equals(runtime.BlockReasonCode, code, StringComparison.Ordinal))
        {
            return;
        }
        LogChargingHeld(logger, code, runtime.JourneyId, runtime.VehicleKey, null);
        runtime.SetBlockReason(code, now);
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把这一步的计划（一条 <c>CHARGER</c> 腿）与业务状态（<c>CHARGING</c>）暂存、保存、发出：承诺之后是 <c>ALLOCATED</c> 那一对，
    /// RIoT 确认建单之后是 <c>EN_ROUTE</c> 那一对。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 消息 id 是 <c>StableUuid(journeyId|用途)</c>（<see cref="ChargingJourneyShape"/>），修订号取旅程行上承诺时按车计数器定下的基准：
    /// <c>ALLOCATED</c> 用基准，<c>EN_ROUTE</c> 用基准加一，正好是每趟旅程在两条流上预留的量。所以重跑与补发都是同一张：暂存原语对已在
    /// 发件箱里的 id 什么也不加，这里也就不再发——没确认的由 <see cref="ReplayChargingSnapshotsAsync"/> 补发。<c>EN_ROUTE</c> 那一对排进去时，
    /// 还没被确认的 <c>ALLOCATED</c> 那一对同一次保存里退役：被取代的快照不补发。
    /// </para>
    /// <para>
    /// <c>batteryState</c> 是承诺时记在旅程上的那一版投影，不现读（同一个消息 id 重发时载荷必须逐字相同）。发不出去（没连着、握手中）不算失败。
    /// </para>
    /// </remarks>
    private async Task PublishChargingSnapshotsAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        bool enRoute,
        CancellationToken cancellationToken)
    {
        SessionRecoveryRow? session = await dbContext.SessionRecoveries.AsNoTracking()
            .SingleOrDefaultAsync(row => row.AgvId == runtime.AgvId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return;
        }

        string planMessageId = enRoute ? ChargingJourneyShape.EnRoutePlanMessageId(runtime.JourneyId) : runtime.PlanMessageId;
        string stateMessageId = enRoute
            ? ChargingJourneyShape.EnRouteStateMessageId(runtime.JourneyId)
            : runtime.VehicleBusinessMessageId;
        long step = enRoute ? 1 : 0;
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (enRoute &&
            !await dbContext.ProtocolOutbox.AsNoTracking()
                .AnyAsync(row => row.MessageId == planMessageId || row.MessageId == stateMessageId, cancellationToken)
                .ConfigureAwait(false))
        {
            // The ALLOCATED pair, if the vehicle has not acknowledged it, is retired in the save that stages the pair replacing
            // it: the replay sends unacknowledged rows oldest first, and the vehicle tears the session down on a snapshot below
            // the revision it has adopted (see RetireSupersededSnapshotAsync). A snapshot states what is current; the EN_ROUTE
            // pair says it.
            await FenceSupersededSnapshotAsync(runtime.PlanMessageId, cancellationToken).ConfigureAwait(false);
            await FenceSupersededSnapshotAsync(runtime.VehicleBusinessMessageId, cancellationToken).ConfigureAwait(false);
        }
        bool stagedPlan = await OnboardJourneyPublisher.StageUpcomingStopPlanAsync(
            store,
            planMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            JourneyPlanBuilder.ChargerPlan(runtime, stop, enRoute, runtime.PlanRevision + step),
            now,
            cancellationToken).ConfigureAwait(false);
        bool stagedState = await OnboardJourneyPublisher.StageVehicleBusinessStateAsync(
            store,
            stateMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            JourneyPlanBuilder.ChargingBusinessState(
                runtime.VehicleBusinessRevision + step,
                enRoute ? ChargingCycleWireStates.EnRoute : ChargingCycleWireStates.Allocated,
                PublishedBatteryState(runtime)),
            // A millisecond after the plan, so a replay -- which sends in creation order -- sends the plan first too.
            now.AddMilliseconds(1),
            cancellationToken).ConfigureAwait(false);
        if (!stagedPlan && !stagedState)
        {
            return;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (stagedPlan)
        {
            await SendIdleReturnSnapshotAsync(planMessageId, cancellationToken).ConfigureAwait(false);
        }
        if (stagedState)
        {
            await SendIdleReturnSnapshotAsync(stateMessageId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>车重连之后，把这一趟没被确认的途中快照按原 id 补发，不产生第二份。</summary>
    private async Task ReplayChargingSnapshotsAsync(
        JourneyRuntimeRow runtime,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        SessionRecoveryRow? session = await dbContext.SessionRecoveries.AsNoTracking()
            .SingleOrDefaultAsync(row => row.AgvId == runtime.AgvId, cancellationToken).ConfigureAwait(false);
        if (session is null ||
            !await SessionLiveness.HeardFromAsync(dbContext, runtime.AgvId, session.SessionGeneration, now, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }
        try
        {
            await publisher.ReplayPendingForSessionAsync(
                    runtime.AgvId,
                    session.SessionGeneration,
                    new HashSet<string>(StringComparer.Ordinal)
                    {
                        runtime.PlanMessageId,
                        runtime.VehicleBusinessMessageId,
                        ChargingJourneyShape.EnRoutePlanMessageId(runtime.JourneyId),
                        ChargingJourneyShape.EnRouteStateMessageId(runtime.JourneyId),
                        ChargingJourneyShape.ArrivedPlanMessageId(runtime.JourneyId),
                        ChargingJourneyShape.ChargingStateMessageId(runtime.JourneyId),
                        ChargingJourneyShape.ClearingPlanMessageId(runtime.JourneyId),
                        ChargingJourneyShape.ClearingStateMessageId(runtime.JourneyId),
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // Not on the line this moment; the next round, or the next reconnect, sends them.
        }
    }
}
