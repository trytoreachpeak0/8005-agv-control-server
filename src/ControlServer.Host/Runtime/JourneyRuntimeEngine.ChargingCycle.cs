using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

// 批次9-07（control-server#405）：充电单 SUCCESS 之后——到桩、开始充电、充满。离桩与桩占用的释放不在这里：充满那一刻旅程收尾，
// 之后由充电分配每轮开头的清扫凭三项确认释放（ChargingAllocator）。
public sealed partial class JourneyRuntimeEngine
{
    private static readonly Action<ILogger, string, string, int, Exception?> LogChargerArrived =
        LoggerMessage.Define<string, string, int>(
            LogLevel.Information,
            new EventId(2257, nameof(LogChargerArrived)),
            "Charging journey {JourneyId}: vehicle {VehicleKey} is proven at charger {StationId}; its reservation is now an " +
            "occupancy.");

    private static readonly Action<ILogger, string, string, int, int, Exception?> LogChargingStarted =
        LoggerMessage.Define<string, string, int, int>(
            LogLevel.Information,
            new EventId(2258, nameof(LogChargingStarted)),
            "Charging journey {JourneyId}: vehicle {VehicleKey} reads CHARGING at charger {StationId} ({Battery}%); the cycle is " +
            "CHARGING.");

    private static readonly Action<ILogger, string, string, int, DateTimeOffset, Exception?> LogChargerNotEngaged =
        LoggerMessage.Define<string, string, int, DateTimeOffset>(
            LogLevel.Warning,
            new EventId(2259, nameof(LogChargerNotEngaged)),
            "CHARGER_NOT_ENGAGED: charging journey {JourneyId}: vehicle {VehicleKey} has stood at charger {StationId} since " +
            "{ArrivedAt} and RIoT has never read it CHARGING. This is not a confirmation that it cannot charge (REQ-0175): the " +
            "cycle, the CHARGING purpose and the charger occupancy are kept, no charger is paused and nothing is sent. Someone has " +
            "to look at the vehicle and the charger.");

    private static readonly Action<ILogger, string, string, int, int, int, Exception?> LogChargingComplete =
        LoggerMessage.Define<string, string, int, int, int>(
            LogLevel.Information,
            new EventId(2260, nameof(LogChargingComplete)),
            "Charging journey {JourneyId}: vehicle {VehicleKey} reached {Battery}% at charger {StationId}, the completion " +
            "threshold {Threshold}% of the cycle's frozen policy. The cycle is COMPLETE and the CHARGING purpose released; the " +
            "charger stays occupied until the vehicle has taken its next purpose and left it.");

    private static readonly Action<ILogger, string, string, string, int, DateTimeOffset, Exception?> LogChargingLossEscalated =
        LoggerMessage.Define<string, string, string, int, DateTimeOffset>(
            LogLevel.Warning,
            new EventId(2261, nameof(LogChargingLossEscalated)),
            "{ReasonCode}: charging journey {JourneyId} of vehicle {VehicleKey} at charger {StationId} has been without that " +
            "observation since {Since}. Only this alarm escalates (REQ-0287): no order is ended, nothing is released or " +
            "reassigned, the vehicle is not moved and no charger is paused. The vehicle may still be on the charger.");

    private static readonly Action<ILogger, string, string, int, DateTimeOffset, Exception?> LogBatteryTelemetryLossEscalated =
        LoggerMessage.Define<string, string, int, DateTimeOffset>(
            LogLevel.Warning,
            new EventId(2262, nameof(LogBatteryTelemetryLossEscalated)),
            "CHARGING_BATTERY_TELEMETRY_LOST: charging journey {JourneyId} of vehicle {VehicleKey} at charger {StationId} has had " +
            "no fresh battery reading since {Since}; the completion judgement is suspended. Only this alarm escalates (REQ-0287): " +
            "nothing is ended, released, reassigned or moved, and the last reading before the gap is not taken as still true.");

    /// <summary>
    /// 充电单已是终态 <c>SUCCESS</c>（充电动作已经接上，Q-033）：到桩、开始充电、充满。每一轮只走这一段里的一步，证据不足就什么也不推进。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>到桩</b>（票面第 1 条）：订单那一半——终态 <c>SUCCESS</c>、订单号是意图记下的那一张、车、图、目标站都是这个桩——加车辆那一半
    /// （<see cref="StandsStillAt"/>：在线、启用、空闲、当前图加当前站精确等于这个桩、速度为零、无锁、无在执行的任务、读数新鲜），与空闲返回到等待点同一组。
    /// 全满足才在同一次保存里把 <c>CHARGER</c> 预占转占用、记到桩时刻、暂存「腿 <c>ARRIVED</c>」的计划；任何一样缺失、过期或冲突都保持
    /// <c>EN_ROUTE</c>，下一轮再判，没有超时分支。
    /// </para>
    /// <para>
    /// <b>开始充电</b>（第 2 条）：到桩之后读到新鲜的 <c>batteryState=CHARGING</c> 才判开始——周期进 <c>CHARGING</c>，发 <c>CHARGING</c> 的业务状态。
    /// 到桩那一轮已经读到时同一次保存一起做。到桩超过 <c>JourneyRuntime:OwnOrderRebuildDelay</c> 仍读不到：写
    /// <see cref="ChargingExecutionReasons.ChargerNotEngaged"/>、告警一次，其余一样不动（<c>REQ-0175</c>：那不是充不上的确认）。
    /// </para>
    /// <para>
    /// <b>充满</b>（第 3 条）：新鲜、且接着上一个新鲜样本的电量读数达到<b>本周期冻结的</b>策略版本里的完成阈值（<c>REQ-0282</c>）——
    /// 周期 <c>COMPLETE</c>、放开 <c>CHARGING</c> 用途、旅程收尾，同一次保存；桩仍是占用（<c>REQ-0281</c>）。不为离桩建任何单、不发任何命令。
    /// </para>
    /// <para>
    /// <b>失联三分</b>（第 6 条，<c>REQ-0287</c>）：车载端会话失效不影响这一段——这里的证据全是 RIoT 的，车载端只收快照（没连上就等重连补发）。
    /// RIoT 车辆观测丢失（读不到、报离线）写 <see cref="ChargingExecutionReasons.VehicleObservationLost"/>；电量遥测丢失（读不到电量、读数过期）
    /// 写 <see cref="ChargingExecutionReasons.BatteryTelemetryLost"/> 并打断样本的连续性。两者持续超过 <c>JourneyRuntime:OwnOrderRebuildRepeatWindow</c>
    /// 各升级告警一次。什么也不结束、不释放、不改派、不移动、不暂停桩。
    /// </para>
    /// </remarks>
    private async Task AdvanceAtChargerAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        OrderIntentRow intent,
        RiotOrderObservation order,
        ChargingCycleRow cycle,
        CancellationToken cancellationToken)
    {
        ChargingAllocationBoard board = dispatchRound.Charging.Board;
        RiotVehicleObservation? vehicle;
        try
        {
            vehicle = await vehicleFacts.ReadVehicleAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            vehicle = null;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (vehicle is not { Connected: true })
        {
            // The vehicle may still be on the charger: everything is kept, nothing is sent (REQ-0287).
            board.BreakSampleRun(cycle.CycleId);
            await NameChargingLossAsync(runtime, stop, ChargingExecutionReasons.VehicleObservationLost, now, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        board.Unsay(ChargingAllocationBoard.ChargingLossKey(runtime.JourneyId, ChargingExecutionReasons.VehicleObservationLost));

        if (cycle.ArrivedAt is null)
        {
            await JudgeChargerArrivalAsync(runtime, stop, intent, order, cycle, vehicle, now, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (cycle.WireState == ChargingCycleWireStates.EnRoute)
        {
            await JudgeChargingStartAsync(runtime, stop, cycle, vehicle, now, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (cycle.WireState == ChargingCycleWireStates.Charging)
        {
            await JudgeChargingCompletionAsync(runtime, stop, cycle, vehicle, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task JudgeChargerArrivalAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        OrderIntentRow intent,
        RiotOrderObservation order,
        ChargingCycleRow cycle,
        RiotVehicleObservation vehicle,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        bool exactOrder = !string.IsNullOrWhiteSpace(order.OrderId) &&
                          order.OrderId == intent.OrderId &&
                          order.VehicleKey == runtime.VehicleKey &&
                          order.MapId == runtime.MapId &&
                          order.DestinationStationId == stop.StationRiotId;
        if (!exactOrder || !StandsStillAt(vehicle, runtime, stop.StationRiotId, now))
        {
            // The order says it succeeded, the vehicle does not yet (or the order is not the one we sent): reconciled again
            // next round. No timeout.
            await ClearChargingCodeAsync(runtime, now, cancellationToken).ConfigureAwait(false);
            await NameCheckpointWaitAsync(runtime, cancellationToken).ConfigureAwait(false);
            return;
        }

        StationExclusivityRow? held = await dbContext.Set<StationExclusivityRow>()
            .SingleOrDefaultAsync(
                row => row.MapId == runtime.MapId && row.StationId == stop.StationRiotId &&
                       row.StationKind == StationExclusivityKinds.Charger && row.JourneyId == runtime.JourneyId,
                cancellationToken)
            .ConfigureAwait(false);
        if (held is null)
        {
            // At the charger, and the charger is no longer this journey's (released by hand, control-server#406): nothing is
            // converted, nothing is released, and someone is told. The server cannot take back a vehicle that has stopped.
            await HoldChargingAsync(runtime, ChargingExecutionReasons.ReservationLostAtArrival, now, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await WaitingPointExclusivity.StageOccupyAsync(dbContext, held, now, cancellationToken).ConfigureAwait(false);
        cycle.ArrivedAt = now;
        cycle.Version++;
        bool charging = ReadsChargingFreshly(vehicle, now);
        string? chargingBatteryState = null;
        if (charging)
        {
            chargingBatteryState = await StartChargingAsync(cycle, vehicle, now, cancellationToken).ConfigureAwait(false);
        }
        checkpointWaits.Clear(runtime.VehicleKey);
        runtime.SetBlockReason(null, now);
        runtime.UpdatedAt = now;
        await StageAtChargerSnapshotsAsync(runtime, stop, arrivedPlan: true, chargingBatteryState, now, cancellationToken)
            .ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogChargerArrived(logger, runtime.JourneyId, runtime.VehicleKey, stop.StationRiotId, null);
        if (charging)
        {
            dispatchRound.Charging.Board.ContinuesSampleRun(cycle.CycleId, vehicle.ObservedAt, runtimeOptions.MaximumEvidenceAge);
            LogChargingStarted(logger, runtime.JourneyId, runtime.VehicleKey, stop.StationRiotId, vehicle.BatteryPercent ?? -1, null);
        }
        await SendAtChargerSnapshotsAsync(runtime, arrivedPlan: true, charging, cancellationToken).ConfigureAwait(false);
    }

    private async Task JudgeChargingStartAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        ChargingCycleRow cycle,
        RiotVehicleObservation vehicle,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (ReadsChargingFreshly(vehicle, now))
        {
            string batteryState = await StartChargingAsync(cycle, vehicle, now, cancellationToken).ConfigureAwait(false);
            runtime.SetBlockReason(null, now);
            runtime.UpdatedAt = now;
            dispatchRound.Charging.Board.Unsay(
                ChargingAllocationBoard.ChargingLossKey(runtime.JourneyId, ChargingExecutionReasons.ChargerNotEngaged));
            await StageAtChargerSnapshotsAsync(runtime, stop, arrivedPlan: false, batteryState, now, cancellationToken)
                .ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            dispatchRound.Charging.Board.ContinuesSampleRun(cycle.CycleId, vehicle.ObservedAt, runtimeOptions.MaximumEvidenceAge);
            LogChargingStarted(logger, runtime.JourneyId, runtime.VehicleKey, stop.StationRiotId, vehicle.BatteryPercent ?? -1, null);
            await SendAtChargerSnapshotsAsync(runtime, arrivedPlan: false, chargingState: true, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (cycle.ArrivedAt is { } arrivedAt && now - arrivedAt >= runtimeOptions.OwnOrderRebuildDelay)
        {
            if (!string.Equals(runtime.BlockReasonCode, ChargingExecutionReasons.ChargerNotEngaged, StringComparison.Ordinal))
            {
                runtime.SetBlockReason(ChargingExecutionReasons.ChargerNotEngaged, now);
                runtime.UpdatedAt = now;
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            if (dispatchRound.Charging.Board.FirstTime(
                    ChargingAllocationBoard.ChargingLossKey(runtime.JourneyId, ChargingExecutionReasons.ChargerNotEngaged)))
            {
                LogChargerNotEngaged(logger, runtime.JourneyId, runtime.VehicleKey, stop.StationRiotId, arrivedAt, null);
            }
            return;
        }

        await ClearChargingCodeAsync(runtime, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task JudgeChargingCompletionAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        ChargingCycleRow cycle,
        RiotVehicleObservation vehicle,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ChargingAllocationBoard board = dispatchRound.Charging.Board;
        if (vehicle.BatteryPercent is not int battery || !Fresh(vehicle, now))
        {
            // Telemetry lost: the completion judgement waits, and the reading before the gap is never taken as still true --
            // the run of samples is broken, so the first fresh one after it only starts observing again.
            board.BreakSampleRun(cycle.CycleId);
            await NameChargingLossAsync(runtime, stop, ChargingExecutionReasons.BatteryTelemetryLost, now, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        board.Unsay(ChargingAllocationBoard.ChargingLossKey(runtime.JourneyId, ChargingExecutionReasons.BatteryTelemetryLost));
        bool continuous = board.ContinuesSampleRun(cycle.CycleId, vehicle.ObservedAt, runtimeOptions.MaximumEvidenceAge);
        await ClearChargingCodeAsync(runtime, now, cancellationToken).ConfigureAwait(false);
        // REQ-0282: the cycle's own version, frozen when it was allocated -- not the one in effect now.
        ChargingPolicyVersion policy = await chargingPolicy.ReadFrozenAsync(cycle.ChargingPolicyVersion, cancellationToken)
            .ConfigureAwait(false);
        int threshold = policy.Content.ChargingCompletionThresholdPercent;
        if (!continuous || battery < threshold)
        {
            return;
        }

        // One save: the cycle COMPLETE, the CHARGING purpose released, the journey closed with its two snapshots. The charger
        // stays this vehicle's occupancy (REQ-0281); it is released only on the three confirmations (ChargingAllocator).
        cycle.WireState = ChargingCycleWireStates.Complete;
        cycle.CompletedAt = now;
        cycle.LastSampleAt = vehicle.ObservedAt;
        cycle.LastSamplePercent = battery;
        cycle.Version++;
        stop.Status = JourneyStopStatuses.Completed;
        checkpointWaits.Clear(runtime.VehicleKey);
        await JourneyPurposeClaimRelease.StageAsync(
                dbContext, runtime.JourneyId, now, ChargingExecutionReasons.Completed, cancellationToken)
            .ConfigureAwait(false);
        // What the closure supersedes is not replayed after it.
        foreach (string superseded in ChargingSnapshotIds(runtime))
        {
            await FenceSupersededSnapshotAsync(superseded, cancellationToken).ConfigureAwait(false);
        }
        await JourneyClosure.StageChargingCompleteAsync(
                dbContext, runtime, stop,
                BatteryEligibility.Project(vehicle, DispatchBatteryPolicy.From(policy), observationFresh: true),
                now, cancellationToken)
            .ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        board.BreakSampleRun(cycle.CycleId);
        LogChargingComplete(logger, runtime.JourneyId, runtime.VehicleKey, battery, stop.StationRiotId, threshold, null);
        await JourneyClosure.SendAsync(publisher, dbContext, runtime.AgvId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>周期进 <c>CHARGING</c>（暂存）：记下第一次看到充电的时刻与这个样本。答发给车的 <c>batteryState</c>（按周期冻结的策略投影）。</summary>
    private async Task<string> StartChargingAsync(
        ChargingCycleRow cycle,
        RiotVehicleObservation vehicle,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cycle.WireState = ChargingCycleWireStates.Charging;
        cycle.FirstChargingSeenAt = now;
        cycle.LastSampleAt = vehicle.ObservedAt;
        cycle.LastSamplePercent = vehicle.BatteryPercent;
        cycle.Version++;
        ChargingPolicyVersion policy = await chargingPolicy.ReadFrozenAsync(cycle.ChargingPolicyVersion, cancellationToken)
            .ConfigureAwait(false);
        return BatteryEligibility.Project(vehicle, DispatchBatteryPolicy.From(policy), observationFresh: true);
    }

    /// <summary>
    /// 一种失联：第一次写上它的码；持续超过 <c>JourneyRuntime:OwnOrderRebuildRepeatWindow</c> 升级告警一次。别的什么也不做。
    /// </summary>
    private async Task NameChargingLossAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        string code,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(runtime.BlockReasonCode, code, StringComparison.Ordinal))
        {
            runtime.SetBlockReason(code, now);
            runtime.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (runtime.BlockReasonSince is not { } since ||
            now - since <= runtimeOptions.OwnOrderRebuildRepeatWindow ||
            !dispatchRound.Charging.Board.FirstTime(ChargingAllocationBoard.ChargingLossKey(runtime.JourneyId, code)))
        {
            return;
        }

        if (code == ChargingExecutionReasons.BatteryTelemetryLost)
        {
            LogBatteryTelemetryLossEscalated(logger, runtime.JourneyId, runtime.VehicleKey, stop.StationRiotId, since, null);
        }
        else
        {
            LogChargingLossEscalated(logger, code, runtime.JourneyId, runtime.VehicleKey, stop.StationRiotId, since, null);
        }
    }

    /// <summary>这一段自己写的码（失联两种、到桩不充电）在证据回来之后清掉；别的码不碰。</summary>
    private async Task ClearChargingCodeAsync(JourneyRuntimeRow runtime, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (runtime.BlockReasonCode is not (ChargingExecutionReasons.VehicleObservationLost
            or ChargingExecutionReasons.BatteryTelemetryLost
            or ChargingExecutionReasons.ChargerNotEngaged))
        {
            return;
        }
        dispatchRound.Charging.Board.Unsay(ChargingAllocationBoard.ChargingLossKey(runtime.JourneyId, runtime.BlockReasonCode));
        runtime.SetBlockReason(null, now);
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 到桩的计划（腿 <c>ARRIVED</c>）与开始充电的业务状态（<c>CHARGING</c>）暂存，与周期的推进同一次保存。修订号取「发件箱里这辆车这条流上最大的 + 1」
    /// （同收尾快照）；被它们取代、还没被确认的途中快照同一次保存里退役，不再补发。
    /// </summary>
    private async Task StageAtChargerSnapshotsAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        bool arrivedPlan,
        string? chargingBatteryState,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        SessionRecoveryRow? session = await dbContext.SessionRecoveries.AsNoTracking()
            .SingleOrDefaultAsync(row => row.AgvId == runtime.AgvId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return;
        }

        if (arrivedPlan)
        {
            await FenceSupersededSnapshotAsync(runtime.PlanMessageId, cancellationToken).ConfigureAwait(false);
            await FenceSupersededSnapshotAsync(ChargingJourneyShape.EnRoutePlanMessageId(runtime.JourneyId), cancellationToken)
                .ConfigureAwait(false);
            long revision = await JourneyClosure.HighestSentRevisionAsync(
                                    dbContext, runtime.AgvId, "UpcomingStopPlanSnapshot", cancellationToken)
                                .ConfigureAwait(false) + 1
                            ?? runtime.PlanRevision + 2;
            await OnboardJourneyPublisher.StageUpcomingStopPlanAsync(
                store,
                ChargingJourneyShape.ArrivedPlanMessageId(runtime.JourneyId),
                runtime.AgvId,
                session.SessionGeneration,
                new UpcomingStopPlanProjection(revision, [JourneyPlanBuilder.ChargerLeg(runtime, stop, "ARRIVED")]),
                now,
                cancellationToken).ConfigureAwait(false);
        }

        if (chargingBatteryState is not null)
        {
            await FenceSupersededSnapshotAsync(runtime.VehicleBusinessMessageId, cancellationToken).ConfigureAwait(false);
            await FenceSupersededSnapshotAsync(ChargingJourneyShape.EnRouteStateMessageId(runtime.JourneyId), cancellationToken)
                .ConfigureAwait(false);
            long revision = await JourneyClosure.HighestSentRevisionAsync(
                                    dbContext, runtime.AgvId, "VehicleBusinessStateSnapshot", cancellationToken)
                                .ConfigureAwait(false) + 1
                            ?? runtime.VehicleBusinessRevision + 2;
            await OnboardJourneyPublisher.StageVehicleBusinessStateAsync(
                store,
                ChargingJourneyShape.ChargingStateMessageId(runtime.JourneyId),
                runtime.AgvId,
                session.SessionGeneration,
                JourneyPlanBuilder.ChargingBusinessState(revision, ChargingCycleWireStates.Charging, chargingBatteryState),
                // A millisecond after the plan, so a replay -- which sends in creation order -- sends the plan first too.
                now.AddMilliseconds(1),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendAtChargerSnapshotsAsync(
        JourneyRuntimeRow runtime,
        bool arrivedPlan,
        bool chargingState,
        CancellationToken cancellationToken)
    {
        if (arrivedPlan)
        {
            await SendIdleReturnSnapshotAsync(ChargingJourneyShape.ArrivedPlanMessageId(runtime.JourneyId), cancellationToken)
                .ConfigureAwait(false);
        }
        if (chargingState)
        {
            await SendIdleReturnSnapshotAsync(ChargingJourneyShape.ChargingStateMessageId(runtime.JourneyId), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>这趟充电旅程在途中发过的每一张快照的 id：<c>ALLOCATED</c>、<c>EN_ROUTE</c>、到桩、开始充电。</summary>
    private static IReadOnlyList<string> ChargingSnapshotIds(JourneyRuntimeRow runtime) =>
    [
        runtime.PlanMessageId,
        runtime.VehicleBusinessMessageId,
        ChargingJourneyShape.EnRoutePlanMessageId(runtime.JourneyId),
        ChargingJourneyShape.EnRouteStateMessageId(runtime.JourneyId),
        ChargingJourneyShape.ArrivedPlanMessageId(runtime.JourneyId),
        ChargingJourneyShape.ChargingStateMessageId(runtime.JourneyId),
    ];

    /// <summary>新鲜地读到 <c>batteryState=CHARGING</c>。</summary>
    private bool ReadsChargingFreshly(RiotVehicleObservation vehicle, DateTimeOffset now) =>
        string.Equals(vehicle.BatteryState, BatteryEligibility.ChargingBatteryState, StringComparison.Ordinal) && Fresh(vehicle, now);

    private bool Fresh(RiotVehicleObservation vehicle, DateTimeOffset now) =>
        vehicle.ObservedAt <= now && now - vehicle.ObservedAt <= runtimeOptions.MaximumEvidenceAge;

    /// <summary>
    /// 到点证据的车辆那一半，非业务停靠（等待点、充电桩）共用（<c>REQ-0295</c>）：车在线、启用、空闲、当前图加当前站精确等于
    /// <paramref name="stationId"/>、速度为零、无锁、无在执行的任务，读数新鲜。
    /// </summary>
    private bool StandsStillAt(RiotVehicleObservation vehicle, JourneyRuntimeRow runtime, int stationId, DateTimeOffset now) =>
        vehicle.Connected && vehicle.Enabled &&
        vehicle.ProcState == "IDLE" &&
        vehicle.CurrentMap == runtime.MapIdentity &&
        vehicle.CurrentStationId == stationId &&
        vehicle.Speed == 0 &&
        vehicle.LockStatus == 0 &&
        string.IsNullOrWhiteSpace(vehicle.OrderTaskId) &&
        Fresh(vehicle, now);
}
