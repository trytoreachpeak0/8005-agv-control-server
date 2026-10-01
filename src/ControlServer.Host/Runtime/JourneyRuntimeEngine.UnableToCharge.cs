using System.Globalization;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

// 批次9-08（control-server#406）：充不上即暂停这个桩并告警，车保持原位，由有权限的人现场挪车并确认清桩。确认的判定在
// Charging/ManualStationClearance.cs（车载端与 Host 共用），这里是引擎的那三段：自动形成「已确认充不上」、清桩中每一轮、清桩之后的旅程收尾。
public sealed partial class JourneyRuntimeEngine
{
    private static readonly Action<ILogger, string, string, int, string, string, Exception?> LogUnableToChargeConfirmed =
        LoggerMessage.Define<string, string, int, string, string>(
            LogLevel.Warning,
            new EventId(2264, nameof(LogUnableToChargeConfirmed)),
            "UNABLE_TO_CHARGE_CONFIRMED: vehicle {VehicleKey} (journey {JourneyId}) could not charge at charger {StationId}: " +
            "RIoT returned {FailureCode} for the start-charging action and order {UpperId} ended in HANG, with no CHARGING read " +
            "this cycle. The charger is paused for all 8005 allocation (root cause UNKNOWN; nothing is blamed on the vehicle or " +
            "the charger) and the vehicle stays where it is: no order, no move, no transport, no idle return, no other charger. " +
            "A person with R-11 or R-13 has to move it off the charger and confirm the charger clear, from the vehicle or the " +
            "Host entry. The charger's allocation returns only with a ChargingStationRecoveryConfirmation.");

    private static readonly Action<ILogger, int, string, string, DateTimeOffset, string, Exception?> LogClearedOldOrderUnsettled =
        LoggerMessage.Define<int, string, string, DateTimeOffset, string>(
            LogLevel.Warning,
            new EventId(2265, nameof(LogClearedOldOrderUnsettled)),
            "CHARGING_CLEARED_OLD_ORDER_UNSETTLED: the charger {StationId} that vehicle {VehicleKey} (journey {JourneyId}) " +
            "could not charge at was confirmed clear at {ClearedAt}, but its old charge order is still {Disposition} in RIoT. " +
            "The charger is released only once that order has ended (REQ-0178); until then it stays held. Cancel the order " +
            "in RIoT: this server cancels it at most once, and only while JourneyRuntime:UnableToChargeOldOrderCancelEnabled " +
            "is on (REQ-0148, baseline v1.9.0), and never again once that cancel went unconfirmed.");

    private static readonly Action<ILogger, string, string, string, string, Exception?> LogClearedOldOrderCancelNotConfirmed =
        LoggerMessage.Define<string, string, string, string>(
            LogLevel.Warning,
            new EventId(2270, nameof(LogClearedOldOrderCancelNotConfirmed)),
            "Charging journey {JourneyId} of vehicle {VehicleKey}: the cancel of old charge order {OrderId} after the manual " +
            "clearance was not confirmed ({Outcome}). It is not sent again; the charger stays held until RIoT reads the order " +
            "ended. Cancel it in RIoT.");

    private static readonly Action<ILogger, string, string, int, string, Exception?> LogClearedChargerReleased =
        LoggerMessage.Define<string, string, int, string>(
            LogLevel.Information,
            new EventId(2267, nameof(LogClearedChargerReleased)),
            "Charging journey {JourneyId} of vehicle {VehicleKey}: charger {StationId} was confirmed clear and the old order " +
            "has ended ({Disposition}); the charger's exclusivity and the vehicle's purpose are released and the journey " +
            "closes. The charger's allocation hold, if any, stays.");

    /// <summary>
    /// 去桩的单 RIoT 报 <c>HANG</c>：「已确认充不上」的严格事实（<see cref="UnableToChargeFacts"/>）在两次相隔不超过
    /// <c>JourneyRuntime:MaximumEvidenceAge</c> 的新鲜观测里都成立，才在一次保存里形成确认，答真；否则什么也不做，答假，交给
    /// <see cref="NameStalledOrderAsync"/> 照原样写 <c>ORDER_HANG</c>（<c>REQ-0175</c>：单独的 <c>HANG</c> 不是充不上）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>同一次保存</b>（票面第 3、5 条）：暂停事件（<see cref="ChargingStationAllocationHoldRow"/>，触发来源
    /// <see cref="ChargingStationHoldTriggers.UnableToChargeConfirmed"/>，根因 <c>UNKNOWN</c>，写入即不可改）、周期进清桩中
    /// （<c>UNABLE_TO_CHARGE</c>、<see cref="ChargingCyclePhases.Clearing"/>）、清桩记录开始、车的用途由 <c>CHARGING</c> 改为
    /// <c>CLEARING_MAINTENANCE</c>、旅程写 <see cref="ChargingExecutionReasons.UnableToChargeClearing"/>、两张清桩中快照。任一样没落库，其余都不落库；
    /// 下一轮按同一组事实再判，幂等键（<c>StableUuid(UNABLE_TO_CHARGE_CONFIRMED|周期)</c>）不变，所以同一次充不上被看到多少轮都只有一条事件。
    /// </para>
    /// <para>
    /// <b>不碰急停、不交故障模型</b>（票面第 9 条，实读）：<c>HANG</c> 从不交给 <c>VehicleFaultCoordinator</c>——<see cref="NameStalledOrderAsync"/>
    /// 对 <c>HANG</c> 只写码，途中仓门监看对 <c>HANG</c> 直接返回（<see cref="ObserveDoorsInTransitAsync"/>）。所以车停在桩上的这个 <c>HANG</c>
    /// 不会被升级成急停、落进「出不去的环」；用例 <c>AnUnableToChargeHangOnTheChargerIsNeverEscalatedToAnEmergencyStop</c> 钉住它。
    /// </para>
    /// <para>
    /// <b>原桩重试为 0</b>（<c>REQ-0284</c>）：之后这辆车在这个周期里没有任何以原桩为目标的单，也不发 <c>CONTINUE_FROM_HANG</c>——清桩中的分支什么命令都不发，
    /// 桩暂停着，分配也不会再选它。
    /// </para>
    /// </remarks>
    private async Task<bool> ConfirmUnableToChargeAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        OrderIntentRow intent,
        RiotOrderObservation order,
        ChargingCycleRow cycle,
        CancellationToken cancellationToken)
    {
        ChargingAllocationBoard board = dispatchRound.Charging.Board;
        string key = UnableToChargeFacts.ContinuityKey(cycle.CycleId);
        RiotOrderMissionFacts missions = await dispatchRound.Charging.Occupancy.OrderMissions
            .ReadOrderMissionFactsAsync(stop.UpperId, cancellationToken).ConfigureAwait(false);
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
        StationExclusivityRow? reservation = await dbContext.Set<StationExclusivityRow>().AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.MapId == runtime.MapId && row.StationId == stop.StationRiotId &&
                       row.StationKind == StationExclusivityKinds.Charger,
                cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset now = timeProvider.GetUtcNow();
        IReadOnlyList<string> missing = UnableToChargeFacts.Missing(
            runtime, stop, intent.OrderId, cycle.FirstChargingSeenAt, reservation?.JourneyId, order, missions, vehicle, now,
            runtimeOptions.MaximumEvidenceAge);
        if (missing.Count > 0)
        {
            board.BreakSampleRun(key);
            board.ForgetObserved(key);
            return false;
        }

        // "Final" and "continuous": the same facts in two fresh observations no further apart than the evidence age. The first
        // one only starts observing; this round the HANG is named as any other (ORDER_HANG).
        DateTimeOffset firstSeen = board.FirstObserved(key, now);
        if (!board.ContinuesSampleRun(key, missions.ObservedAt, runtimeOptions.MaximumEvidenceAge))
        {
            return false;
        }

        RiotOrderMissionFact start = missions.Missions.Single(mission =>
            string.Equals(mission.Type, "act", StringComparison.OrdinalIgnoreCase) &&
            mission.ActionId == UnableToChargeFacts.ChargeActionId && mission.ActionParam1 == UnableToChargeFacts.StartChargingParam);
        string dedupKey = ChargingStationHoldTriggers.UnableToChargeConfirmed + "|" + cycle.CycleId;
        dbContext.Add(new ChargingStationAllocationHoldRow
        {
            HoldId = JourneyPlanBuilder.StableGuid(cycle.CycleId, "unable-to-charge-hold"),
            IdempotencyKey = JourneyPlanBuilder.StableGuid(dedupKey, "charging-station-hold"),
            Trigger = ChargingStationHoldTriggers.UnableToChargeConfirmed,
            RootCause = ChargingHoldRootCauses.Unknown,
            MapId = cycle.MapId,
            StationId = cycle.StationId,
            ChargerRosterVersion = cycle.ChargerRosterVersion,
            VehicleKey = runtime.VehicleKey,
            ReservationRecordId = reservation!.RecordId,
            CycleId = cycle.CycleId,
            UpperId = stop.UpperId,
            OrderId = intent.OrderId,
            // RIoT gives no time for the arrival or for the act's start; what this server knows is when it first saw the vehicle
            // standing on the charger with the failed act and the HANG (the first of the two observations).
            ArrivedAt = firstSeen,
            ChargingStartedAt = null,
            FailedAt = firstSeen,
            FinalHangAt = now,
            ConfirmedAt = now,
            HeldAt = now,
            RawPositionJson = JsonSerializer.Serialize(vehicle, SerializerOptions),
            RawOrderJson = JsonSerializer.Serialize(order, SerializerOptions),
            RawActionResultJson = JsonSerializer.Serialize(missions, SerializerOptions),
            RawBatteryJson = JsonSerializer.Serialize(
                new { vehicle!.BatteryPercent, vehicle.BatteryState, vehicle.ObservedAt }, SerializerOptions),
            EvidenceReference = string.Create(
                CultureInfo.InvariantCulture,
                $"riot:detailByUpperId/{stop.UpperId}@{missions.ObservedAt:O}; act({start.ActionId},{start.ActionParam1}) resultCode={start.ResultCode}"),
            RiotBuild = UnableToChargeFacts.VerifiedRiotBuild,
            RiotContractVersion = UnableToChargeFacts.VerifiedRiotContract,
        });
        dbContext.Add(new StationClearanceRow
        {
            ClearanceId = JourneyPlanBuilder.StableGuid(cycle.CycleId, "station-clearance"),
            CycleId = cycle.CycleId,
            VehicleKey = runtime.VehicleKey,
            MapId = cycle.MapId,
            StationId = cycle.StationId,
            StartedAt = now,
            AssistantsJson = "[]",
        });
        cycle.WireState = ChargingCycleWireStates.UnableToCharge;
        cycle.Phase = ChargingCyclePhases.Clearing;
        cycle.Version++;
        await VehiclePurposeClaimTransition.StageAsync(
                dbContext, runtime.VehicleKey, runtime.JourneyId, VehiclePurposes.Charging, VehiclePurposes.ClearingMaintenance,
                now, ChargingStationHoldTriggers.UnableToChargeConfirmed, cancellationToken)
            .ConfigureAwait(false);
        checkpointWaits.Clear(runtime.VehicleKey);
        runtime.SetBlockReason(ChargingExecutionReasons.UnableToChargeClearing, now);
        runtime.UpdatedAt = now;
        bool staged = await StageClearingSnapshotsAsync(runtime, stop, now, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        board.BreakSampleRun(key);
        board.ForgetObserved(key);
        LogUnableToChargeConfirmed(
            logger, runtime.VehicleKey, runtime.JourneyId, stop.StationRiotId,
            UnableToChargeFacts.VerifiedFailureCode.ToString(CultureInfo.InvariantCulture), stop.UpperId, null);
        if (staged)
        {
            await SendClearingSnapshotsAsync(runtime, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// 清桩中的一轮：车保持原位，什么命令都不发；人工清桩确认之前只补发清桩中的快照，确认之后旧单对账到终态的那一轮释放并收尾。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>出口</b>（<c>held-state-needs-governed-exit</c>）：唯一的出口是人工清桩确认（<see cref="ManualStationClearance"/>）。没人来确认，车就一直留着、桩一直暂停着——
    /// 这正是用户 09-29 定的「由有权限的人现场挪车」；告警在形成确认的那一刻发过一次（事件 2264），看板上一直挂着
    /// <see cref="ChargingExecutionReasons.UnableToChargeClearing"/>。
    /// </para>
    /// <para>
    /// <b>确认之后旧单还没收敛</b>（仍 <c>HANG</c>、读不到、结果未知，<c>REQ-0178</c>）：桩不放、用途不放、继续对账，写
    /// <see cref="ChargingExecutionReasons.ClearedOldOrderUnsettled"/>；超过 <c>JourneyRuntime:OwnOrderRebuildRepeatWindow</c> 告警一次（事件 2265），
    /// 请人在 RIoT 里取消旧单。开关打开（默认）时本服务端先取消一次（<c>REQ-0148</c> 情形一，基线 <c>v1.9.0</c>，<c>CP-0010</c>），
    /// 见 <see cref="CancelClearedOldOrderWhenEnabledAsync"/>；取消之后照样等对账读到终态。
    /// </para>
    /// <para>
    /// <b>执行前重判前提</b>：释放那一刻重读清桩记录、这一轮重新读旧单，释放本身带着读到的周期版本与独占持有者（<see cref="ChargerClearanceRelease"/>），
    /// 人工确认在同一刻放过了就什么也不写。<b>清桩中有人在 RIoT 里取消旧单：不重建</b>——这一段不经 <see cref="NameStalledOrderAsync"/>，
    /// 「取消即重建」的登记（<see cref="RecordOrderEndedInRiotAsync"/>）走不到这里。
    /// </para>
    /// </remarks>
    private async Task AdvanceClearingAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        ChargingCycleRow cycle,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        await ReplayChargingSnapshotsAsync(runtime, now, cancellationToken).ConfigureAwait(false);
        if (await StageClearingSnapshotsAsync(runtime, stop, now, cancellationToken).ConfigureAwait(false))
        {
            // The vehicle had no session when the confirmation was formed, or the round crashed after it: staged now, once.
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await SendClearingSnapshotsAsync(runtime, cancellationToken).ConfigureAwait(false);
        }

        StationClearanceRow? clearance = await dbContext.Set<StationClearanceRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.CycleId == cycle.CycleId, cancellationToken).ConfigureAwait(false);
        if (clearance is not { CompletedAt: { } clearedAt })
        {
            await SetChargingCodeAsync(runtime, ChargingExecutionReasons.UnableToChargeClearing, now, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        RiotOrderObservation order;
        try
        {
            order = await vehicleFacts.ReconcileByUpperIdAsync(stop.UpperId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            order = new RiotOrderObservation(stop.UpperId, RiotOrderObservationKind.Unknown, null);
        }
        string disposition = ManualStationClearance.Disposition(order, stop.UpperId);
        string unsettledKey = "clearing-old-order-unsettled:" + runtime.JourneyId;
        if (!ManualStationClearance.Settled(disposition))
        {
            await SetChargingCodeAsync(runtime, ChargingExecutionReasons.ClearedOldOrderUnsettled, now, cancellationToken)
                .ConfigureAwait(false);
            await CancelClearedOldOrderWhenEnabledAsync(runtime, stop, order, cancellationToken).ConfigureAwait(false);
            if (now - clearedAt > runtimeOptions.OwnOrderRebuildRepeatWindow && dispatchRound.Charging.Board.FirstTime(unsettledKey))
            {
                LogClearedOldOrderUnsettled(
                    logger, stop.StationRiotId, runtime.VehicleKey, runtime.JourneyId, clearedAt, disposition, null);
            }
            return;
        }

        if (await ChargerClearanceRelease.ReleaseAsync(
                dbContext, cycle.CycleId, cycle.Version, ChargingExecutionReasons.UnableToChargeCleared, now, cancellationToken)
                .ConfigureAwait(false))
        {
            dispatchRound.Charging.Board.Unsay(unsettledKey);
            LogClearedChargerReleased(logger, runtime.JourneyId, runtime.VehicleKey, stop.StationRiotId, disposition, null);
        }
        await CloseClearedChargingAsync(runtime, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 已取得人工清桩确认、旧单仍 <c>HANG</c>（清桩闭环还没完成）：开关 <c>JourneyRuntime:UnableToChargeOldOrderCancelEnabled</c> 打开（默认）时由本服务端取消这张旧单，一次；关着什么也不做。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>依据</b>：<c>REQ-0148</c>（基线 <c>v1.9.0</c>，<c>CP-0010</c>）情形一——车辆按 <c>REQ-0178</c> 进入清桩中闭环后、清桩完成之前，
    /// 本服务端自建的充电周期旧单尚未终结，可以取消；白名单第 1.3 节同口径。<c>REQ-0178</c> 要求清桩前旧单到取消终态，所以默认开。
    /// 开关留作立即收紧的手段（白名单第五节第二行：软件负责人可立即收紧，恢复要批准人批准）；关着时旧单只对账，事件 2265 请人在 RIoT 里取消。
    /// </para>
    /// <para>
    /// <b>打开后取消哪张单、在什么条件下</b>（比条文窄，条文从进入清桩中就允许）：只取消这个周期自己 <c>upperId</c> 下的那张——归属凭本库的
    /// 停靠与意图证明，不凭 <c>upperId</c> 的形态；只在取得人工清桩确认之后（<c>REQ-0179</c> 的完成证明，清桩记录的 <c>CompletedAt</c>）、
    /// 清桩闭环完成之前（<c>REQ-0178</c>：旧单终结才完成清桩，在这里就是桩释放、周期结束的那一轮；这一支只在周期仍是 <c>CLEARING</c> 时走到）——
    /// 正落在 <c>REQ-0148</c> 情形一「进入清桩中闭环后、清桩完成之前」的窗口里，比它窄：不在人到场之前取消；这一轮重读到它恰好是 <c>HANG</c>（其它任何状态都不发）、且执行它的就是这个周期的车
    /// （RIoT 读到别的车、或读不到执行车，都不发：条文「未由非 8005 管辖的车辆执行」要先证明）；命令审计里这张单还没有取消记录（只发一次，没确认也不重发，
    /// 事件 2270）。取消本身什么也不放、不重建：桩与用途照旧要等之后某一轮读到旧单已终结才释放，车在那之前一直留着，与人在 RIoT 里取消走同一条路。
    /// </para>
    /// </remarks>
    private async Task CancelClearedOldOrderWhenEnabledAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        RiotOrderObservation order,
        CancellationToken cancellationToken)
    {
        if (!runtimeOptions.UnableToChargeOldOrderCancelEnabled || orderCommands is null ||
            order is not { Kind: RiotOrderObservationKind.Active, OrderState: RiotOrderState.Hang, OrderId: string orderId } ||
            !string.Equals(order.VehicleKey, runtime.VehicleKey, StringComparison.Ordinal) ||
            await OwnCancelIssuedAsync(orderId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        RiotOrderCommandRecord cancel = await orderCommands.IssueAsync(
                RiotOrderCommandKind.Cancel,
                new RiotOrderCommandTarget(runtime.AgvId, stop.UpperId, orderId),
                "control-server#406: REQ-0148 (v1.9.0) case 1, the old charge order of a clearance confirmed after an unable-to-charge",
                faultGeneration: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (!cancel.Succeeded)
        {
            LogClearedOldOrderCancelNotConfirmed(logger, runtime.JourneyId, runtime.VehicleKey, orderId, cancel.Outcome.ToString(), null);
        }
    }

    /// <summary>
    /// 这趟充电旅程的周期已由人工清桩收尾（<see cref="ChargingExecutionReasons.ClearedEndings"/>）：旅程收尾、发两张收尾快照（计划为空、
    /// <c>activePurpose</c> 为空、<c>NOT_CHARGING</c>），答真。不是这样的周期答假、什么也不做。
    /// </summary>
    /// <remarks>
    /// 释放在先、收尾在后，各一次保存：人工确认那条路从不写旅程行，引擎这一轮才收尾；两次保存之间崩掉，下一轮读到结束了的周期照样收尾。
    /// 车之后按 <c>REQ-0180</c> 走常规派车检查：位置、订单、运动、安全任一读不到都挡在门外，直到对账完成；还要充电就回统一电量队列，
    /// 唯一的桩暂停着时排队并告警「无合格桩」（事件 2246，<c>CHARGER_ALLOCATION_HELD</c>），名册非空，不进人工充电等待。
    /// </remarks>
    private async Task<bool> CloseClearedChargingAsync(JourneyRuntimeRow runtime, CancellationToken cancellationToken)
    {
        ChargingCycleRow? cleared = (await dbContext.Set<ChargingCycleRow>().AsNoTracking()
                .Where(row => row.JourneyId == runtime.JourneyId && row.Phase == ChargingCyclePhases.Ended)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(row => row.EndReason is { } reason && ChargingExecutionReasons.ClearedEndings.Contains(reason));
        if (cleared is null)
        {
            return false;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        JourneyStopRow stop = await dbContext.Set<JourneyStopRow>()
            .SingleAsync(row => row.JourneyId == runtime.JourneyId, cancellationToken).ConfigureAwait(false);
        stop.Status = JourneyStopStatuses.Completed;
        checkpointWaits.Clear(runtime.VehicleKey);
        // Released with the charger already; a claim still standing (a crash between the two saves cannot leave one, but a
        // journey closed here holds nothing afterwards either way).
        await JourneyPurposeClaimRelease.StageAsync(dbContext, runtime.JourneyId, now, cleared.EndReason!, cancellationToken)
            .ConfigureAwait(false);
        foreach (string superseded in ChargingSnapshotIds(runtime).Concat(ClearingSnapshotIds(runtime)))
        {
            await FenceSupersededSnapshotAsync(superseded, cancellationToken).ConfigureAwait(false);
        }
        await JourneyClosure.StageChargingAsync(dbContext, runtime, cleared.EndReason!, now, cancellationToken)
            .ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        dispatchRound.Charging.Board.Unsay("clearing-old-order-unsettled:" + runtime.JourneyId);
        await JourneyClosure.SendAsync(publisher, dbContext, runtime.AgvId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 清桩中的计划与业务状态（还没暂存过时）暂存，不保存：计划留那条 <c>CHARGER</c> 腿（<c>ARRIVED</c>，车停在那个桩上；车载端靠它取原桩的站点号，
    /// 调度 09-30 对齐第 6 条），业务状态 <c>activePurpose=CLEARING_MAINTENANCE</c>、<c>chargingCycleState=UNABLE_TO_CHARGE</c>。被它们取代、
    /// 还没被确认的途中快照同一次保存里退役。答这一次是否暂存了。
    /// </summary>
    private async Task<bool> StageClearingSnapshotsAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        SessionRecoveryRow? session = await dbContext.SessionRecoveries.AsNoTracking()
            .SingleOrDefaultAsync(row => row.AgvId == runtime.AgvId, cancellationToken).ConfigureAwait(false);
        string planId = ChargingJourneyShape.ClearingPlanMessageId(runtime.JourneyId);
        string stateId = ChargingJourneyShape.ClearingStateMessageId(runtime.JourneyId);
        if (session is null ||
            await dbContext.ProtocolOutbox.AsNoTracking()
                .AnyAsync(row => row.MessageId == planId || row.MessageId == stateId, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }

        foreach (string superseded in ChargingSnapshotIds(runtime))
        {
            await FenceSupersededSnapshotAsync(superseded, cancellationToken).ConfigureAwait(false);
        }
        long planRevision = await JourneyClosure.HighestSentRevisionAsync(
                                    dbContext, runtime.AgvId, "UpcomingStopPlanSnapshot", cancellationToken)
                                .ConfigureAwait(false) + 1
                            ?? runtime.PlanRevision + 2;
        long stateRevision = await JourneyClosure.HighestSentRevisionAsync(
                                     dbContext, runtime.AgvId, "VehicleBusinessStateSnapshot", cancellationToken)
                                 .ConfigureAwait(false) + 1
                             ?? runtime.VehicleBusinessRevision + 2;
        await OnboardJourneyPublisher.StageUpcomingStopPlanAsync(
            store,
            planId,
            runtime.AgvId,
            session.SessionGeneration,
            new UpcomingStopPlanProjection(planRevision, [JourneyPlanBuilder.ChargerLeg(runtime, stop, "ARRIVED")]),
            now,
            cancellationToken).ConfigureAwait(false);
        await OnboardJourneyPublisher.StageVehicleBusinessStateAsync(
            store,
            stateId,
            runtime.AgvId,
            session.SessionGeneration,
            new VehicleBusinessProjection(
                stateRevision, "READY", VehicleActivePurposes.ClearingMaintenance, false, PublishedBatteryState(runtime),
                ChargingCycleWireStates.UnableToCharge, null, []),
            // A millisecond after the plan, so a replay -- which sends in creation order -- sends the plan first too.
            now.AddMilliseconds(1),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task SendClearingSnapshotsAsync(JourneyRuntimeRow runtime, CancellationToken cancellationToken)
    {
        foreach (string messageId in ClearingSnapshotIds(runtime))
        {
            await SendIdleReturnSnapshotAsync(messageId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>清桩中那两张快照的 id：计划在前。</summary>
    private static IReadOnlyList<string> ClearingSnapshotIds(JourneyRuntimeRow runtime) =>
    [
        ChargingJourneyShape.ClearingPlanMessageId(runtime.JourneyId),
        ChargingJourneyShape.ClearingStateMessageId(runtime.JourneyId),
    ];

    private async Task SetChargingCodeAsync(
        JourneyRuntimeRow runtime, string code, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (string.Equals(runtime.BlockReasonCode, code, StringComparison.Ordinal))
        {
            return;
        }
        runtime.SetBlockReason(code, now);
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
