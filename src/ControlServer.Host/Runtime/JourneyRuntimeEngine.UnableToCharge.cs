using System.Globalization;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Dispatch.Criteria;
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
            "The clearance completes and the charger is released only once that order has ended (REQ-0178); until then it " +
            "stays held. End the order " +
            "in RIoT. This server cancels it at most once, and only while JourneyRuntime:UnableToChargeOldOrderCancelEnabled " +
            "is on (off by default; REQ-0148 v1.9.0 case 1), never again once that cancel went unconfirmed.");

    private static readonly Action<ILogger, string, string, int, string, Exception?> LogClearanceExitUnavailable =
        LoggerMessage.Define<string, string, int, string>(
            LogLevel.Warning,
            new EventId(2271, nameof(LogClearanceExitUnavailable)),
            "Vehicle {VehicleKey} (journey {JourneyId}) shows every strict fact of an unable-to-charge at charger {StationId}, " +
            "but the manual station clearance exit is not available ({Reasons}), so it is not paused for a clearance: the HANG " +
            "stays ORDER_HANG and ends in RIoT as before. Configure FieldOperatorRoles:Path with an R-11 or R-13, and " +
            "VehicleFaultRecovery:enabled or FieldOperatorRoles:OnboardClearanceEntryDeclared (control-server#406).");

    private static readonly Action<ILogger, string, string, string, TimeSpan, string, Exception?> LogClearedOldOrderCancelNotConfirmed =
        LoggerMessage.Define<string, string, string, TimeSpan, string>(
            LogLevel.Warning,
            new EventId(2270, nameof(LogClearedOldOrderCancelNotConfirmed)),
            "Charging journey {JourneyId} of vehicle {VehicleKey}: old charge order {OrderId} has not ended {Window} after this " +
            "server cancelled it (still {Disposition} in RIoT). The cancel is not sent again; the clearance does not complete " +
            "and the charger stays held until RIoT reads the order ended. End it in RIoT.");

    private static readonly Action<ILogger, string, string, int, string, string, Exception?> LogOldOrderResumedWhileClearing =
        LoggerMessage.Define<string, string, int, string, string>(
            LogLevel.Warning,
            new EventId(2273, nameof(LogOldOrderResumedWhileClearing)),
            "CHARGING_OLD_ORDER_RESUMED_WHILE_CLEARING: vehicle {VehicleKey} (journey {JourneyId}) is held for a manual clearance " +
            "at charger {StationId}, and its old charge order {UpperId} has left HANG for {Disposition} in RIoT: somebody let it " +
            "go on, and the vehicle may drive back onto the charger while a person is clearing it. Warn the people on site. This " +
            "server sends no emergency stop and no cancel for it (control-server#406 review S5).");

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
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (cycle.FirstChargingSeenAt is null &&
            string.Equals(vehicle?.BatteryState, BatteryEligibility.ChargingBatteryState, StringComparison.Ordinal))
        {
            // REQ-0174 "no CHARGING the whole cycle": a CHARGING read while the order hangs counts as seen, for good -- not
            // only for this round's run (review P1 of control-server#406).
            cycle.FirstChargingSeenAt = now;
            cycle.Version++;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        StationExclusivityRow? reservation = await dbContext.Set<StationExclusivityRow>().AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.MapId == runtime.MapId && row.StationId == stop.StationRiotId &&
                       row.StationKind == StationExclusivityKinds.Charger,
                cancellationToken)
            .ConfigureAwait(false);

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

        // Review M1: formed, the vehicle's only way out is a person's clearance. Where nobody can give one, it is not formed:
        // the HANG stays ORDER_HANG, which still ends in RIoT, and the reason is said once.
        // An engine built without the exit (none registered) cannot vouch for one: treated as not offered.
        if ((clearanceExit is null ? StationClearanceExit.NoEntry : clearanceExit.Unavailable()) is { } unavailable)
        {
            if (board.FirstTime("clearance-exit-unavailable:" + cycle.CycleId))
            {
                LogClearanceExitUnavailable(logger, runtime.VehicleKey, runtime.JourneyId, stop.StationRiotId, unavailable, null);
            }
            board.BreakSampleRun(key);
            board.ForgetObserved(key);
            return false;
        }

        // The purpose first (review item 5): when the claim is not this journey's CHARGING one, nothing is formed.
        if (!await VehiclePurposeClaimTransition.StageAsync(
                dbContext, runtime.VehicleKey, runtime.JourneyId, VehiclePurposes.Charging, VehiclePurposes.ClearingMaintenance,
                now, ChargingStationHoldTriggers.UnableToChargeConfirmed, cancellationToken)
                .ConfigureAwait(false))
        {
            board.BreakSampleRun(key);
            board.ForgetObserved(key);
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
    /// 清桩中的一轮（周期 <see cref="ChargingCyclePhases.Clearing"/>，清桩记录还没完成）：补发清桩中快照；旧单还没终结时（开关打开）先取消一次；人工确认已记下、
    /// 旧单也已终结时<b>完成清桩</b>并放桩；否则保持，写看得见的码。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>顺序</b>（control-server#406 独立审查，调度 10-01 定）：取消在前、完成在后，三者不互相等——
    /// <list type="number">
    /// <item>进入清桩中、清桩还没完成（<c>CompletedAt</c> 为空）时，旧单读到 <c>HANG</c> 就由这里发一次取消（<see cref="CancelOldOrderWhileClearingAsync"/>）。
    /// 它不等人工确认，所以落在 <c>REQ-0148</c>（基线 <c>v1.9.0</c>）情形一的窗口里：进入清桩中闭环后、清桩完成之前。</item>
    /// <item>人工确认随时可以到，先记下来（<see cref="ManualStationClearance"/>）。</item>
    /// <item>只有「人工确认已记下」与「旧单已终结」两样都齐了，才写 <c>CompletedAt</c> 并放桩（<see cref="ChargerClearanceRelease.CompleteAndReleaseAsync"/>，
    /// <c>REQ-0178</c>：取消结果未知时不完成清桩；<c>REQ-0179</c>：人工确认是完成的证明）。哪一样后到，就由哪一边完成。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>保持状态要看得见</b>（<c>held-state-needs-governed-exit</c>）：没人确认时是 <see cref="ChargingExecutionReasons.UnableToChargeClearing"/>
    /// （形成确认那一刻告警过，事件 2264）；确认已记下、旧单还没终结时是 <see cref="ChargingExecutionReasons.ClearedOldOrderUnsettled"/>，从确认起超过
    /// <c>JourneyRuntime:OwnOrderRebuildRepeatWindow</c> 告警一次（事件 2265）；发出的取消过了观察窗口旧单仍没终结告警一次（事件 2270，审查 S3）；
    /// 旧单从 <c>HANG</c> 回到排队或执行——有人在 RIoT 里让它继续，车可能开回桩上——立刻告警一次、码换成
    /// <see cref="ChargingExecutionReasons.OldOrderResumedWhileClearing"/>（事件 2273，审查 S5；不急停、不取消）。出口是人在 RIoT 里把旧单结束——
    /// 开关关着时这本来就是唯一的路。
    /// </para>
    /// <para>
    /// <b>执行前重判前提</b>：完成那一刻按「还没完成、确认已记下」更新、按读到的周期版本与持有者释放，人工确认在同一刻完成过就什么也不写。
    /// <b>清桩中有人在 RIoT 里取消旧单：不重建</b>——这一段不经 <see cref="NameStalledOrderAsync"/>，「取消即重建」的登记
    /// （<see cref="RecordOrderEndedInRiotAsync"/>）走不到这里。
    /// </para>
    /// </remarks>
    private async Task AdvanceClearingAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        OrderIntentRow intent,
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
        DateTimeOffset? confirmedAt = clearance?.ConfirmedAt;
        string unsettledKey = "clearing-old-order-unsettled:" + runtime.JourneyId;

        if (!ManualStationClearance.Settled(disposition))
        {
            // Review S5: out of HANG into a state that can move the vehicle -- somebody let the order go on in RIoT.
            bool resumed = order is
            {
                Kind: RiotOrderObservationKind.Active,
                OrderState: RiotOrderState.Queueing or RiotOrderState.Executing or RiotOrderState.QueuePriority,
            };
            if (resumed && dispatchRound.Charging.Board.FirstTime("clearing-old-order-resumed:" + runtime.JourneyId))
            {
                LogOldOrderResumedWhileClearing(
                    logger, runtime.VehicleKey, runtime.JourneyId, stop.StationRiotId, stop.UpperId,
                    string.Create(CultureInfo.InvariantCulture, $"orderState {order.OrderState}"), null);
            }
            if (clearance is { CompletedAt: null })
            {
                await CancelOldOrderWhileClearingAsync(runtime, stop, intent, order, cancellationToken).ConfigureAwait(false);
                await WarnCancelNotTakenAsync(runtime, intent, disposition, now, cancellationToken).ConfigureAwait(false);
            }
            await SetChargingCodeAsync(
                    runtime,
                    resumed ? ChargingExecutionReasons.OldOrderResumedWhileClearing
                    : confirmedAt is null ? ChargingExecutionReasons.UnableToChargeClearing
                    : ChargingExecutionReasons.ClearedOldOrderUnsettled,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            if (confirmedAt is { } since && now - since > runtimeOptions.OwnOrderRebuildRepeatWindow &&
                dispatchRound.Charging.Board.FirstTime(unsettledKey))
            {
                LogClearedOldOrderUnsettled(
                    logger, stop.StationRiotId, runtime.VehicleKey, runtime.JourneyId, since, disposition, null);
            }
            return;
        }

        if (clearance is not { ConfirmedAt: not null, CompletedAt: null })
        {
            // The old order has ended, nobody has confirmed the charger clear yet: the vehicle stays, waiting for that person.
            await SetChargingCodeAsync(runtime, ChargingExecutionReasons.UnableToChargeClearing, now, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (await ChargerClearanceRelease.CompleteAndReleaseAsync(
                dbContext, clearance.ClearanceId, cycle.CycleId, cycle.Version, ChargingExecutionReasons.UnableToChargeCleared,
                disposition, now, cancellationToken)
                .ConfigureAwait(false))
        {
            dispatchRound.Charging.Board.Unsay(unsettledKey);
            LogClearedChargerReleased(logger, runtime.JourneyId, runtime.VehicleKey, stop.StationRiotId, disposition, null);
        }
        await CloseClearedChargingAsync(runtime, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 清桩中、清桩还没完成、旧单仍 <c>HANG</c>：开关 <c>JourneyRuntime:UnableToChargeOldOrderCancelEnabled</c> 打开时由本服务端取消这张旧单，一次；
    /// 开关默认关，关着什么也不做。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>依据</b>：<c>REQ-0148</c>（基线 <c>v1.9.0</c>，<c>CP-0010</c>）情形一——车辆按 <c>REQ-0178</c> 进入清桩中闭环后、清桩完成之前，本服务端自建的
    /// 充电周期旧单尚未终结，可以取消；白名单第 1.3 节同口径。调用方只在周期是 <c>CLEARING</c>、清桩记录还没完成时调它。
    /// </para>
    /// <para>
    /// <b>为什么默认关</b>（control-server#406 独立审查，调度 10-01 定）：取消一张停在桩上的 <c>HANG</c> 充电单之后，RIoT 会不会在队首插入离桩动作
    /// <c>act(78,2,0)</c>、让车在现场的人旁边动起来，没有任何现场证据（白名单与实验室只记了「下一张单的队首」会插）。这是准入线 1 的风险。关着时出口是
    /// 「人在 RIoT 里结束旧单，服务端读到终态，加上人工确认，放桩」。等 10-08 现场经用户授权在 agv02 上实测过取消的效果，再定要不要打开。
    /// </para>
    /// <para>
    /// <b>打开后取消哪张、在什么条件下</b>，全部同时满足才发：这一轮重读到它恰好是 <c>HANG</c>（其它任何状态都不发）；RIoT 的单号就是这个周期订单意图上的
    /// 那一张（<c>order.OrderId == intent.OrderId</c>，归属凭本库的意图，不凭 <c>upperId</c> 的形态）；执行它的就是这个周期的车（读到别的车、或读不到执行车，
    /// 都不发：条文「未由非 8005 管辖的车辆执行」要先证明）；命令审计里这张单还没有取消记录（只发一次，没确认也不重发，事件 2270）。取消本身什么也不放、
    /// 不重建：桩与用途等之后某一轮读到旧单终结、并且人工确认已记下才完成清桩释放，车在那之前一直留着。
    /// </para>
    /// </remarks>
    private async Task CancelOldOrderWhileClearingAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        OrderIntentRow intent,
        RiotOrderObservation order,
        CancellationToken cancellationToken)
    {
        if (!runtimeOptions.UnableToChargeOldOrderCancelEnabled || orderCommands is null ||
            order is not { Kind: RiotOrderObservationKind.Active, OrderState: RiotOrderState.Hang, OrderId: string orderId } ||
            !string.Equals(orderId, intent.OrderId, StringComparison.Ordinal) ||
            !string.Equals(order.VehicleKey, runtime.VehicleKey, StringComparison.Ordinal) ||
            await OwnCancelIssuedAsync(orderId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // Not judged here (review S3): a cancel RIoT applies a moment later still reads HANG on the read-back. Whether it took is
        // judged by WarnCancelNotTakenAsync, once the settle window has passed.
        _ = await orderCommands.IssueAsync(
                RiotOrderCommandKind.Cancel,
                new RiotOrderCommandTarget(runtime.AgvId, stop.UpperId, orderId),
                "control-server#406: REQ-0148 (v1.9.0) case 1, the old charge order of a cycle in the clearing loop",
                faultGeneration: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 本服务端取消过这张旧单、过了 <c>JourneyRuntime:UnableToChargeOldOrderCancelSettleWindow</c>（默认 60 秒）它仍没终结：告警一次（事件 2270）。
    /// 窗口内转为终态的不告警（审查 S3：取消若是异步生效，发出后那一刻回读到的 <c>HANG</c> 不说明取消没起作用）。不重发、不做别的。
    /// </summary>
    private async Task WarnCancelNotTakenAsync(
        JourneyRuntimeRow runtime,
        OrderIntentRow intent,
        string disposition,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (intent.OrderId is not string orderId)
        {
            return;
        }
        string cancel = RiotCommandTypeNames.For(RiotOrderCommandKind.Cancel);
        DateTimeOffset[] issued = await dbContext.RiotOrderCommandAudit.AsNoTracking()
            .Where(row => row.CommandType == cancel && row.TargetOrderId == orderId)
            .Select(row => row.IssuedAt)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        TimeSpan window = runtimeOptions.UnableToChargeOldOrderCancelSettleWindow;
        if (issued.Length > 0 && now - issued.Min() >= window &&
            dispatchRound.Charging.Board.FirstTime("clearing-cancel-not-taken:" + runtime.JourneyId))
        {
            LogClearedOldOrderCancelNotConfirmed(logger, runtime.JourneyId, runtime.VehicleKey, orderId, window, disposition, null);
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
