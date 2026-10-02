using System.Globalization;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

// 批次9-09（control-server#407）：充电中（周期 CHARGING）的中断与无进展（REQ-0285、0286）。只在充电中那一支接入：判定在这里，
// 两侧隔离之后走批次9-08 的清桩中（JourneyRuntimeEngine.UnableToCharge.cs）。
public sealed partial class JourneyRuntimeEngine
{
    private static readonly Action<ILogger, string, string, string, int, string, Exception?> LogChargingStopIsolated =
        LoggerMessage.Define<string, string, string, int, string>(
            LogLevel.Warning,
            new EventId(2276, nameof(LogChargingStopIsolated)),
            "{Trigger}: vehicle {VehicleKey} (journey {JourneyId}) stopped gaining charge at charger {StationId} ({Evidence}). " +
            "The charger's 8005 allocation and this vehicle's charging eligibility are both paused (root cause UNKNOWN: nothing " +
            "is blamed on the vehicle or the charger, REQ-0286). Charging is not restarted at this charger and no other charger " +
            "is tried; the vehicle stays where it is until a person with R-11 or R-13 confirms the charger clear. The charger " +
            "returns with a ChargingStationRecoveryConfirmation, the vehicle with a vehicle charging eligibility recovery, each " +
            "on its own evidence.");

    private static readonly Action<ILogger, string, string, string, int, string, string, Exception?> LogChargingStopNotIsolated =
        LoggerMessage.Define<string, string, string, int, string, string>(
            LogLevel.Warning,
            new EventId(2278, nameof(LogChargingStopNotIsolated)),
            "{Trigger}: vehicle {VehicleKey} (journey {JourneyId}) stopped gaining charge at charger {StationId} ({Evidence}), " +
            "but neither the charger nor the vehicle is paused -- ALARM ONLY, NOT ISOLATED -- because the way back from a " +
            "pause is not available ({Reasons}). The cycle, the charger occupancy and the vehicle are all kept as they are: " +
            "nothing is restarted, released, reassigned or moved. Someone has to look at the vehicle and the charger. Configure " +
            "FieldOperatorRoles:Path with an R-11 or R-13, and VehicleFaultRecovery:enabled with its credential " +
            "(control-server#407).");

    /// <summary>无进展观察的连续性键：与充满判定的样本连续性分开记。</summary>
    private static string InterruptionRunKey(string cycleId) => "charging-interruption-run:" + cycleId;

    /// <summary>
    /// 充电中、这一轮没有判充满：看中断与无进展（<c>REQ-0285</c>）。电量读数此刻新鲜（调用方判过），<paramref name="continuous"/> 是它是否接着上一个新鲜样本。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>中断</b>：电量低于本周期冻结的完成阈值，<c>batteryState</c> 读得到且不是 <c>CHARGING</c>，并且这样的新鲜读数<b>不间断地持续</b>
    /// <c>JourneyRuntime:ChargingInterruptionConfirmAfter</c>（默认 60 秒）——从第一次读到起，每两个相邻读数的观测时刻往前走、相隔不超过
    /// <c>JourneyRuntime:MaximumEvidenceAge</c>，中间没有读到 <c>CHARGING</c>、没有遥测缺口，直到某一个读数距第一个不少于那么久。第一次只开始观察
    /// （#442 审查 S3a：只隔一个轮询间隔的两次读数可能是 RIoT 的同一份快照）。
    /// 读不到电量、读数过期、读不到车、<c>batteryState</c> 为空都<b>不是</b>「不再是 <c>CHARGING</c>」——那是 <c>REQ-0287</c> 的暂停观察，打断这串读数。
    /// 本服务端从不在充满前让车停（离桩只在它取得下一用途之后，而充满前它取不得），所以不用另判「是不是我们让它停的」。
    /// 同一轮电量已达阈值的，调用方先判了充满：充满优先，那不是中断。
    /// </para>
    /// <para>
    /// <b>无进展</b>：持续 <c>CHARGING</c>。稳定期从第一次看到充电（<see cref="ChargingCycleRow.FirstChargingSeenAt"/>）算起，满了之后第一个连续的
    /// <c>CHARGING</c> 样本开一个观察窗口，起点时刻与电量落库（<see cref="ChargingCycleRow.ObservationWindowStartedAt"/>）。<b>新样本不重置起点</b>。
    /// 窗口满（样本的观测时刻减起点不小于窗口）时：增量小于最小增量就形成确认；够了就从这一个样本另开一个窗口。窗口里出现遥测缺口、车辆观测丢失、
    /// 读到不是 <c>CHARGING</c>、或一个不接着上一个的样本（含进程重启后的第一个），窗口作废、重新起算，不顺延——缺口里电量涨没涨不知道。
    /// 三个参数都取本周期冻结的策略版本（<c>REQ-0282</c>），不问此刻生效的那一版。
    /// </para>
    /// </remarks>
    private async Task ObserveChargingProgressAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        OrderIntentRow intent,
        ChargingCycleRow cycle,
        RiotVehicleObservation vehicle,
        int battery,
        bool continuous,
        ChargingPolicyContent policy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ChargingAllocationBoard board = dispatchRound.Charging.Board;
        string interruptionRun = InterruptionRunKey(cycle.CycleId);
        bool charging = string.Equals(vehicle.BatteryState, BatteryEligibility.ChargingBatteryState, StringComparison.Ordinal);
        if (!charging)
        {
            await ForgetObservationWindowAsync(cycle, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(vehicle.BatteryState) || battery >= policy.ChargingCompletionThresholdPercent)
            {
                // Not known to have stopped, or stopped at the threshold (that is completion's to judge, not an interruption).
                board.BreakSampleRun(interruptionRun);
                return;
            }
            // Review S3a of #442: two reads one poll apart can be one RIoT snapshot; the stop has to have lasted.
            if (!board.ContinuesSampleRun(interruptionRun, vehicle.ObservedAt, runtimeOptions.MaximumEvidenceAge) ||
                board.SampleRunStart(interruptionRun) is not { } stoppedSince ||
                vehicle.ObservedAt - stoppedSince < runtimeOptions.ChargingInterruptionConfirmAfter)
            {
                return;
            }
            await ConfirmChargingStopAsync(
                    runtime, stop, intent, cycle, vehicle, ChargingStationHoldTriggers.InterruptionConfirmed,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"batteryState {vehicle.BatteryState} without a break from {stoppedSince:O} to {vehicle.ObservedAt:O} at {battery}%, below the completion threshold {policy.ChargingCompletionThresholdPercent}%"),
                    now, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        board.BreakSampleRun(interruptionRun);
        if (runtime.BlockReasonCode == ChargingExecutionReasons.InterruptionNotIsolated && continuous)
        {
            // Charging again after an interruption that could only be alarmed about: no longer true.
            await ClearAlarmOnlyCodeAsync(runtime, cycle, ChargingStationHoldTriggers.InterruptionConfirmed, now, cancellationToken)
                .ConfigureAwait(false);
        }
        if (!continuous)
        {
            await ForgetObservationWindowAsync(cycle, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (cycle.FirstChargingSeenAt is not { } chargingSince ||
            vehicle.ObservedAt - chargingSince < TimeSpan.FromSeconds(policy.ProgressStabilizationSeconds))
        {
            return;
        }
        if (cycle.ObservationWindowStartedAt is not { } windowStart || cycle.ObservationWindowStartPercent is not int startPercent)
        {
            await StartObservationWindowAsync(cycle, vehicle.ObservedAt, battery, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (vehicle.ObservedAt - windowStart < TimeSpan.FromSeconds(policy.ProgressObservationWindowSeconds))
        {
            return;
        }
        if (battery - startPercent >= policy.ProgressMinimumIncreasePercent)
        {
            if (runtime.BlockReasonCode == ChargingExecutionReasons.NoProgressNotIsolated)
            {
                await ClearAlarmOnlyCodeAsync(runtime, cycle, ChargingStationHoldTriggers.NoProgressConfirmed, now, cancellationToken)
                    .ConfigureAwait(false);
            }
            await StartObservationWindowAsync(cycle, vehicle.ObservedAt, battery, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ConfirmChargingStopAsync(
                runtime, stop, intent, cycle, vehicle, ChargingStationHoldTriggers.NoProgressConfirmed,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"CHARGING from {startPercent}% at {windowStart:O} to {battery}% at {vehicle.ObservedAt:O}, less than {policy.ProgressMinimumIncreasePercent}% over the {policy.ProgressObservationWindowSeconds} s window after the {policy.ProgressStabilizationSeconds} s stabilization (policy version {cycle.ChargingPolicyVersion})"),
                now, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 中断与无进展的观察都要从头来：中断那一串读数打断、无进展窗口作废（落库）。遥测缺口、车辆观测丢失时调（<c>REQ-0287</c>：恢复后以新鲜、连续的样本重新观察）。
    /// </summary>
    private async Task BreakChargingProgressObservationAsync(ChargingCycleRow cycle, CancellationToken cancellationToken)
    {
        dispatchRound.Charging.Board.BreakSampleRun(InterruptionRunKey(cycle.CycleId));
        await ForgetObservationWindowAsync(cycle, cancellationToken).ConfigureAwait(false);
    }

    private async Task StartObservationWindowAsync(
        ChargingCycleRow cycle, DateTimeOffset at, int percent, CancellationToken cancellationToken)
    {
        cycle.ObservationWindowStartedAt = at;
        cycle.ObservationWindowStartPercent = percent;
        cycle.Version++;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ForgetObservationWindowAsync(ChargingCycleRow cycle, CancellationToken cancellationToken)
    {
        if (cycle.ObservationWindowStartedAt is null && cycle.ObservationWindowStartPercent is null)
        {
            return;
        }
        cycle.ObservationWindowStartedAt = null;
        cycle.ObservationWindowStartPercent = null;
        cycle.Version++;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearAlarmOnlyCodeAsync(
        JourneyRuntimeRow runtime, ChargingCycleRow cycle, string trigger, DateTimeOffset now, CancellationToken cancellationToken)
    {
        dispatchRound.Charging.Board.Unsay(AlarmOnlyKey(cycle.CycleId, trigger));
        runtime.SetBlockReason(null, now);
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string AlarmOnlyKey(string cycleId, string trigger) => $"charging-stop-alarm-only:{trigger}:{cycleId}";

    /// <summary>
    /// 中断或无进展已经确认：<b>一次保存</b>里同时暂停这个桩（<see cref="ChargingStationAllocationHoldRow"/>）与这辆车的充电资格
    /// （<see cref="VehicleChargingEligibilityHoldRow"/>），根因 <c>UNKNOWN</c>；用途 <c>CHARGING</c> → <c>CLEARING_MAINTENANCE</c>，周期进清桩中
    /// （<c>UNABLE_TO_CHARGE</c>、<see cref="ChargingCyclePhases.Clearing"/>），清桩记录开始，旅程写这一种的码，两张清桩中快照。任一样没落库，其余都不落库。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>不在原桩重启、不换桩试充</b>：之后这一趟走清桩中那一支（<see cref="AdvanceClearingAsync"/>），它不建单、不发任何命令；桩暂停着分配不会再选它，
    /// 车的资格暂停着分配不会再给它任何桩（<c>REQ-0286</c>：防「故障车逐桩试错并依次暂停全部桩」）。旧单这时已是 <c>SUCCESS</c>（到桩要它）或查无此单，
    /// 清桩中照常核验它终结之后、加上人工确认才完成清桩。
    /// </para>
    /// <para>
    /// <b>幂等</b>：两行的幂等键都是 <c>StableUuid(触发来源|周期)</c>；同一个周期形成一次之后就进了清桩中，这一段不再被走到。崩在这次保存里什么也不留，
    /// 下一轮按同一组事实再判出同一个键。进程重启之后清桩中的周期不再被判。
    /// </para>
    /// <para>
    /// <b>出口不可用时只告警</b>（control-server#406 M1 的教训）：暂停之后桩与车都只能经 Host 的恢复入口回来，车只能经人工清桩离开清桩中。
    /// 两者缺一（<see cref="StationClearanceExit.IsolationUnavailable"/>）就不隔离：周期、桩占用、车原位都保持，旅程写只告警的码，告警一次说明原因。
    /// 这与票面的「只告警」版同一个形状：不重启、不换桩、不释放、不改派、不移动。
    /// </para>
    /// </remarks>
    private async Task ConfirmChargingStopAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        OrderIntentRow intent,
        ChargingCycleRow cycle,
        RiotVehicleObservation vehicle,
        string trigger,
        string evidence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        bool interruption = trigger == ChargingStationHoldTriggers.InterruptionConfirmed;
        // An engine built without the exit (none registered) cannot vouch for one: treated as not offered.
        string? unavailable = clearanceExit is null
            ? StationClearanceExit.NoEntry + "," + StationClearanceExit.NoRecoveryEntry
            : clearanceExit.IsolationUnavailable();
        if (unavailable is not null ||
            !await VehiclePurposeClaimTransition.StageAsync(
                    dbContext, runtime.VehicleKey, runtime.JourneyId, VehiclePurposes.Charging, VehiclePurposes.ClearingMaintenance,
                    now, trigger, cancellationToken)
                .ConfigureAwait(false))
        {
            await SetChargingCodeAsync(
                    runtime,
                    interruption ? ChargingExecutionReasons.InterruptionNotIsolated : ChargingExecutionReasons.NoProgressNotIsolated,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            if (dispatchRound.Charging.Board.FirstTime(AlarmOnlyKey(cycle.CycleId, trigger)))
            {
                LogChargingStopNotIsolated(
                    logger, trigger, runtime.VehicleKey, runtime.JourneyId, stop.StationRiotId, evidence,
                    unavailable ?? "the vehicle's purpose is not this journey's CHARGING claim", null);
            }
            return;
        }

        StationExclusivityRow? reservation = await dbContext.Set<StationExclusivityRow>().AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.MapId == runtime.MapId && row.StationId == stop.StationRiotId &&
                       row.StationKind == StationExclusivityKinds.Charger && row.JourneyId == runtime.JourneyId,
                cancellationToken)
            .ConfigureAwait(false);
        string dedupKey = trigger + "|" + cycle.CycleId;
        string evidenceReference = string.Create(
            CultureInfo.InvariantCulture, $"riot:vehicle/{runtime.VehicleKey}@{vehicle.ObservedAt:O}; {evidence}");
        string rawBattery = JsonSerializer.Serialize(
            new { vehicle.BatteryPercent, vehicle.BatteryState, vehicle.ObservedAt }, SerializerOptions);
        dbContext.Add(new ChargingStationAllocationHoldRow
        {
            HoldId = JourneyPlanBuilder.StableGuid(cycle.CycleId, trigger + "-station-hold"),
            IdempotencyKey = JourneyPlanBuilder.StableGuid(dedupKey, "charging-station-hold"),
            Trigger = trigger,
            RootCause = ChargingHoldRootCauses.Unknown,
            MapId = cycle.MapId,
            StationId = cycle.StationId,
            ChargerRosterVersion = cycle.ChargerRosterVersion,
            VehicleKey = runtime.VehicleKey,
            ReservationRecordId = reservation?.RecordId,
            CycleId = cycle.CycleId,
            UpperId = stop.UpperId,
            OrderId = intent.OrderId,
            ArrivedAt = cycle.ArrivedAt,
            ChargingStartedAt = cycle.FirstChargingSeenAt,
            FailedAt = vehicle.ObservedAt,
            ConfirmedAt = now,
            HeldAt = now,
            RawPositionJson = JsonSerializer.Serialize(vehicle, SerializerOptions),
            RawBatteryJson = rawBattery,
            EvidenceReference = evidenceReference,
        });
        dbContext.Add(new VehicleChargingEligibilityHoldRow
        {
            HoldId = JourneyPlanBuilder.StableGuid(cycle.CycleId, trigger + "-vehicle-hold"),
            IdempotencyKey = JourneyPlanBuilder.StableGuid(dedupKey, "vehicle-charging-eligibility-hold"),
            VehicleKey = runtime.VehicleKey,
            CycleId = cycle.CycleId,
            Reason = trigger,
            HeldAt = now,
            EvidenceReference = evidenceReference,
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
        runtime.SetBlockReason(
            interruption ? ChargingExecutionReasons.InterruptionClearing : ChargingExecutionReasons.NoProgressClearing, now);
        runtime.UpdatedAt = now;
        bool staged = await StageClearingSnapshotsAsync(runtime, stop, now, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        dispatchRound.Charging.Board.BreakSampleRun(cycle.CycleId);
        dispatchRound.Charging.Board.BreakSampleRun(InterruptionRunKey(cycle.CycleId));
        LogChargingStopIsolated(logger, trigger, runtime.VehicleKey, runtime.JourneyId, stop.StationRiotId, evidence, null);
        if (staged)
        {
            await SendClearingSnapshotsAsync(runtime, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 清桩中「等人确认」时旅程上写的码：按这个周期的桩暂停是因为什么形成的——中断、无进展各有自己的码，充不上（和没有暂停的）照旧
    /// <see cref="ChargingExecutionReasons.UnableToChargeClearing"/>。
    /// </summary>
    /// <remarks>
    /// 中断与无进展之后，车可能还在充电（无进展可能只是涨得慢）：那时人工确认会以「车仍在充电」被拒，完成前的重核也挡住它。所以这两种先读一次车，
    /// 读到 <c>CHARGING</c> 就写 <see cref="ChargingExecutionReasons.ClearingVehicleStillCharging"/>、告警一次（事件 2280），不静默等着。读不到车不算在充电。
    /// </remarks>
    private async Task<string> ClearingWaitCodeAsync(
        JourneyRuntimeRow runtime, ChargingCycleRow cycle, CancellationToken cancellationToken)
    {
        string[] triggers = await dbContext.Set<ChargingStationAllocationHoldRow>().AsNoTracking()
            .Where(row => row.CycleId == cycle.CycleId)
            .Select(row => row.Trigger)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        string waiting = triggers.Contains(ChargingStationHoldTriggers.InterruptionConfirmed, StringComparer.Ordinal)
            ? ChargingExecutionReasons.InterruptionClearing
            : triggers.Contains(ChargingStationHoldTriggers.NoProgressConfirmed, StringComparer.Ordinal)
                ? ChargingExecutionReasons.NoProgressClearing
                : ChargingExecutionReasons.UnableToChargeClearing;
        if (waiting == ChargingExecutionReasons.UnableToChargeClearing)
        {
            return waiting;
        }

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
        string stillChargingKey = "clearing-vehicle-still-charging:" + runtime.JourneyId;
        if (vehicle is { Connected: true } &&
            string.Equals(vehicle.BatteryState, BatteryEligibility.ChargingBatteryState, StringComparison.Ordinal))
        {
            if (dispatchRound.Charging.Board.FirstTime(stillChargingKey))
            {
                LogClearingVehicleStillCharging(
                    logger, runtime.VehicleKey, runtime.JourneyId, cycle.StationId, vehicle.BatteryPercent ?? -1, null);
            }
            return ChargingExecutionReasons.ClearingVehicleStillCharging;
        }
        dispatchRound.Charging.Board.Unsay(stillChargingKey);
        return waiting;
    }

    private static readonly Action<ILogger, string, string, int, int, Exception?> LogClearingVehicleStillCharging =
        LoggerMessage.Define<string, string, int, int>(
            LogLevel.Warning,
            new EventId(2280, nameof(LogClearingVehicleStillCharging)),
            "CHARGING_CLEARING_VEHICLE_STILL_CHARGING: vehicle {VehicleKey} (journey {JourneyId}) is held for a manual clearance " +
            "at charger {StationId} after an interruption or no charging progress, and RIoT reads it still charging ({Battery}%). " +
            "A clearance confirmation is refused while it charges and the clearance cannot complete. On site: end the charging " +
            "first, then move the vehicle off the charger, then confirm the charger clear (control-server#407).");
}
