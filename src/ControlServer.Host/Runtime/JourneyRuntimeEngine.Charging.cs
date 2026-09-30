using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

// 批次9-06（control-server#404）：充电旅程从承诺到去桩途中的推进。承诺（周期、CHARGING 用途占有、CHARGER 预占、旅程行、停靠与订单意图，
// 同一次保存）由派车轮里的充电分配形成（ChargingAllocator）；这里发计划与业务状态、过出发前安全门、建单与对账、途中监看、失败收尾。
// 到桩、充电中、充满、离桩与释放在批次9-07。
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
    /// <b>结果未知时五样全保持</b>（<c>REQ-0283</c>、<c>REQ-0173</c>）：车辆、充电意图、目标桩、预占、周期都不动；单号不换（意图按
    /// <c>upperId</c> 复用，<see cref="MovementDispatchService.ReconcileOrCreateAsync"/> 对发过的单只对账不再建）；不换桩、不释放。
    /// 电量继续下降不改变任何一样，只由等人告警升级（<c>REQ-0169</c>，<c>WaitingJourneyWatch</c>）。
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
    /// <b>单到了终态 <c>SUCCESS</c></b>：充电动作已经接上，本票到此为止——不收尾、不释放、不改周期，原样留给批次9-07。
    /// </para>
    /// <para>
    /// <b>不按超时释放。</b>这里没有一个会因为等久了而放车、放桩或换单号的分支。
    /// </para>
    /// </remarks>
    private async Task AdvanceChargingAsync(JourneyRuntimeRow runtime, CancellationToken cancellationToken)
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
            await HoldChargingAsync(runtime, ChargingExecutionReasons.CycleMissing, now, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ReplayChargingSnapshotsAsync(runtime, now, cancellationToken).ConfigureAwait(false);
        // Staged under ids derived from the journey, so every round after the first adds nothing: this is also what stages them
        // for a vehicle that had no session when the commitment was made, or after a crash between a save and its snapshots.
        await PublishChargingSnapshotsAsync(
                runtime, stop, enRoute: cycle.WireState != ChargingCycleWireStates.Allocated, cancellationToken)
            .ConfigureAwait(false);

        if (await store.IsNeverSentAsync(intent, cancellationToken).ConfigureAwait(false))
        {
            // The reservation is this journey's, or the vehicle is not sent: a charger it does not hold may be anyone's.
            if (!await dbContext.Set<StationExclusivityRow>().AsNoTracking()
                    .AnyAsync(
                        row => row.MapId == runtime.MapId && row.StationId == stop.StationRiotId &&
                               row.StationKind == StationExclusivityKinds.Charger && row.JourneyId == runtime.JourneyId,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                await HoldChargingAsync(runtime, ChargingExecutionReasons.ReservationNotHeld, now, cancellationToken)
                    .ConfigureAwait(false);
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
                return;
            }
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

        RiotOrderObservation order = await vehicleFacts.ReconcileByUpperIdAsync(stop.UpperId, cancellationToken)
            .ConfigureAwait(false);
        if (order is { Kind: RiotOrderObservationKind.Terminal, OrderState: RiotOrderState.Success })
        {
            // The charge action took: the rest of the cycle is batch 9-07's. A code a stalled order left is not true any more.
            checkpointWaits.Clear(runtime.VehicleKey);
            if (runtime.BlockReasonCode is not null)
            {
                runtime.SetBlockReason(null, now);
                runtime.UpdatedAt = now;
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            return;
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
        RiotVehicleObservation vehicle;
        RiotVehicleSafetyObservation safety;
        try
        {
            vehicle = await vehicleFacts.ReadVehicleAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false);
            safety = await vehicleSafety.ReadVehicleSafetyAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            return;
        }

        DateTimeOffset readAt = timeProvider.GetUtcNow();
        bool stoppedWithoutOrder = vehicle.Connected && vehicle.ProcState == "IDLE" && vehicle.Speed == 0 &&
                                   string.IsNullOrWhiteSpace(vehicle.OrderTaskId) &&
                                   vehicle.ObservedAt <= readAt && readAt - vehicle.ObservedAt <= runtimeOptions.MaximumEvidenceAge &&
                                   safety.MotionState == RiotVehicleMotionState.Stopped;
        if (!stoppedWithoutOrder)
        {
            return;
        }

        checkpointWaits.Clear(runtime.VehicleKey);
        await ChargingEnding.StageAsync(dbContext, runtime, stop, ChargingExecutionReasons.OrderEnded, readAt, cancellationToken)
            .ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogChargingEnded(
            logger, runtime.JourneyId, runtime.VehicleKey, ChargingExecutionReasons.OrderEnded, stop.StationRiotId, null);
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
