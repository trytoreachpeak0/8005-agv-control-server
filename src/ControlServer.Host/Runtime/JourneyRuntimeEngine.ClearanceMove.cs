using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Host.Runtime.RouteGraph;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

// 批次9-11（control-server#409）：清桩中的车由本服务端开往等待点，到点即完成清桩（REQ-0178，REQ-0179 的系统证明）。开关
// JourneyRuntime:ClearanceToWaitingPointEnabled 默认关，关着时清桩中与 control-server#406 逐字相同：车原地等人工清桩。
// 承诺（选点与原子预占）在清桩中那一支的「旧单已终结、还没人确认」处；之后每一轮从这里推进：建单前复核、建单与对账、到点完成、失败分流。
public sealed partial class JourneyRuntimeEngine
{
    private static readonly Action<ILogger, string, string, int, int, long, Exception?> LogClearanceMoveCommitted =
        LoggerMessage.Define<string, string, int, int, long>(
            LogLevel.Warning,
            new EventId(2290, nameof(LogClearanceMoveCommitted)),
            "CHARGING_CLEARANCE_TO_WAITING_POINT: vehicle {VehicleKey} (journey {JourneyId}) could not charge at charger {StationId}; " +
            "its old order has ended and it is now committed to waiting point {WaitingPoint} (route cost {CostMm} mm). It will " +
            "drive there by itself once the pre-departure gate passes: people on site, mind the vehicle. Arriving completes the " +
            "clearance and releases the charger; the charger's allocation hold stays (control-server#409).");

    private static readonly Action<ILogger, string, string, string, Exception?> LogClearanceNoWaitingPoint =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2291, nameof(LogClearanceNoWaitingPoint)),
            "CHARGING_CLEARANCE_NO_WAITING_POINT: vehicle {VehicleKey} (journey {JourneyId}) may leave its charger for a waiting " +
            "point, but none is eligible ({Excluded}). It stays where it is and no other station is guessed (REQ-0178); a manual " +
            "station clearance still completes it.");

    private static readonly Action<ILogger, string, string, string, Exception?> LogClearanceMoveWithdrawn =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(2292, nameof(LogClearanceMoveWithdrawn)),
            "Clearance move of vehicle {VehicleKey} (journey {JourneyId}) withdrawn before any order went out ({Why}); its waiting " +
            "point reservation is released and the clearing is judged again next round.");

    private static readonly Action<ILogger, string, string, string, string, Exception?> LogClearanceMoveHeld =
        LoggerMessage.Define<string, string, string, string>(
            LogLevel.Warning,
            new EventId(2293, nameof(LogClearanceMoveHeld)),
            "CHARGING_CLEARANCE_MOVE_HELD: clearance move {UpperId} of vehicle {VehicleKey} (journey {JourneyId}) is held ({Why}): " +
            "the vehicle, its waiting point, its purpose and the charger all stay, no other point is chosen and this server sends " +
            "no cancel. Check the order and the vehicle in RIoT.");

    private static readonly Action<ILogger, string, string, string, Exception?> LogClearanceMoveEnded =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2294, nameof(LogClearanceMoveEnded)),
            "CHARGING_CLEARANCE_MOVE_ENDED: clearance move {UpperId} of vehicle {VehicleKey} (journey {JourneyId}) was ended by a " +
            "person before it arrived and the vehicle is proven stopped. It is not rebuilt and this clearing does not set off by " +
            "itself again: a person with R-11 or R-13 has to confirm the charger clear (control-server#404's rule for charging " +
            "orders, applied to the clearing).");

    private static readonly Action<ILogger, string, string, int, Exception?> LogClearedAtWaitingPoint =
        LoggerMessage.Define<string, string, int>(
            LogLevel.Information,
            new EventId(2295, nameof(LogClearedAtWaitingPoint)),
            "Charging journey {JourneyId}: vehicle {VehicleKey} arrived at waiting point {WaitingPoint}; the clearance is complete " +
            "(system proof, REQ-0179), the charger's exclusivity and the vehicle's purpose are released and the waiting point is " +
            "now its occupancy. The charger's allocation hold stays.");

    private static readonly Action<ILogger, string, string, string, Exception?> LogClearanceMoveNotStarted =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(2296, nameof(LogClearanceMoveNotStarted)),
            "Vehicle {VehicleKey} (journey {JourneyId}) does not set off for a waiting point this round: {Why}.");

    /// <summary>这趟旅程还在进行的那一次清桩移动（带跟踪）；没有为空。</summary>
    private async Task<JourneyStopRow?> ActiveClearanceMoveAsync(JourneyRuntimeRow runtime, CancellationToken cancellationToken)
    {
        JourneyStopRow[] moves = await dbContext.Set<JourneyStopRow>()
            .Where(row => row.JourneyId == runtime.JourneyId && row.StopRole == JourneyStopRoles.WaitingPoint &&
                          row.Status != JourneyStopStatuses.Completed && row.Status != JourneyStopStatuses.Removed)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return moves.Where(ClearanceMoveShape.IsClearanceMove).SingleOrDefault();
    }

    /// <summary>
    /// 清桩中、旧单已终结、还没人确认清桩、清桩还没完成：开关打开时试着为这辆车原子预占一个等待点（<c>REQ-0178</c>）。答这一轮旅程上该写的码——
    /// 开关关着、这个周期已不再自动出发、或这一轮没承诺成，答的就是原来那个等人确认的码或说明为什么的码。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>出发前提</b>，每一条都在承诺这一刻读：开关打开；这个周期没有一次被人结束的清桩移动（<see cref="ClearanceMoveReleaseReasons.Ended"/>）；车新鲜地静止停在原桩上
    /// 且不在充电（不在桩上的车——被人挪走、断电、拖走——不由服务端开走，只等人工）；出发前安全门（车况、投运、车载端会话，与充电、空闲返回出发前同一道）。
    /// 安全门不过就不承诺：不让一辆出不了门的车占着等待点。
    /// </para>
    /// <para>
    /// <b>选点</b>：与空闲返回同一个等待点集合、同一个判法（<see cref="IdleReturnEvaluator.ExcludePointAsync"/>：当前地图、专用角色、白名单、实时目录、
    /// 没被预占或占用、路网可达），排除这个周期查无此单失败过的点，代价最小者、平手取站号小的。没有合格点就原地排队、告警一次，<b>不猜别的站点</b>。
    /// </para>
    /// <para>
    /// <b>承诺</b>：<see cref="ClearanceWaitingPointCommitment.TryCommitAsync"/>，一次保存；输了（站被别的承诺先拿到）什么也不写，下一轮重评。
    /// 建单在下一轮，建单前再复核一次。
    /// </para>
    /// </remarks>
    private async Task<string> TryStartClearanceMoveAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow charger,
        ChargingCycleRow cycle,
        RiotMapStationCatalogSnapshot currentMap,
        string waiting,
        CancellationToken cancellationToken)
    {
        if (!runtimeOptions.ClearanceToWaitingPointEnabled)
        {
            return waiting;
        }

        var released = await dbContext.Set<StationExclusivityRecordRow>().AsNoTracking()
            .Where(row => row.JourneyId == runtime.JourneyId && row.StationKind == StationExclusivityKinds.WaitingPoint &&
                          row.ReleaseReason != null)
            .Select(row => new { row.StationId, row.ReleaseReason })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (released.Any(row => row.ReleaseReason == ClearanceMoveReleaseReasons.Ended))
        {
            return ChargingExecutionReasons.ClearanceMoveEnded;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        RiotVehicleObservation? vehicle = await ReadVehicleOrNullAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false);
        if (vehicle is null || !StandsStillAt(vehicle, runtime, cycle.StationId, now) ||
            string.Equals(vehicle.BatteryState, BatteryEligibility.ChargingBatteryState, StringComparison.Ordinal))
        {
            NotStarted(runtime, "the vehicle is not read standing still on its charger, not charging");
            return ChargingExecutionReasons.ClearanceVehicleOffCharger;
        }

        IReadOnlyList<string> gaps = await IdleReturnDepartureGapsAsync(runtime, cancellationToken).ConfigureAwait(false);
        if (gaps.Count > 0)
        {
            NotStarted(runtime, "pre-departure gate: " + string.Join(", ", gaps));
            return ChargingExecutionReasons.ClearanceDepartureNotProven;
        }

        HashSet<int> failedBefore =
        [
            .. released.Where(row => row.ReleaseReason == ClearanceMoveReleaseReasons.NeverAppeared).Select(row => row.StationId),
        ];
        (IdleReturnPointCandidate? chosen, WaitingPointRegistrationVersion? registration, string excluded) =
            await ChooseClearanceWaitingPointAsync(runtime, vehicle.CurrentStationId!.Value, currentMap, failedBefore, cancellationToken)
                .ConfigureAwait(false);
        string noPointKey = "clearance-no-waiting-point:" + runtime.JourneyId;
        if (chosen is null)
        {
            if (dispatchRound.Charging.Board.FirstTime(noPointKey))
            {
                LogClearanceNoWaitingPoint(logger, runtime.VehicleKey, runtime.JourneyId, excluded, null);
            }
            return ChargingExecutionReasons.ClearanceNoWaitingPoint;
        }
        dispatchRound.Charging.Board.Unsay(noPointKey);

        int attempt = await dbContext.Set<JourneyStopRow>().AsNoTracking()
            .CountAsync(row => row.JourneyId == runtime.JourneyId && row.StopRole == JourneyStopRoles.WaitingPoint, cancellationToken)
            .ConfigureAwait(false) + 1;
        WaitingPointEntry point = registration!.Points.Single(item => item.MapId == runtime.MapId && item.StationId == chosen.StationId);
        (JourneyStopRow stop, OrderIntent intent) = ClearanceMoveShape.Build(runtime, charger, attempt, point.StationId, point.StationName, now);
        ClearanceWaitingPointCommitOutcome outcome = await ClearanceWaitingPointCommitment.TryCommitAsync(
                dbContext, runtime.VehicleKey, runtime.JourneyId, runtime.MapId, point.StationId, registration.Version, stop, intent,
                charger, now, cancellationToken)
            .ConfigureAwait(false);
        if (outcome != ClearanceWaitingPointCommitOutcome.Committed)
        {
            NotStarted(runtime, $"the commitment to waiting point {point.StationId} was refused ({outcome})");
            return waiting;
        }

        LogClearanceMoveCommitted(logger, runtime.VehicleKey, runtime.JourneyId, cycle.StationId, point.StationId, chosen.CostMm, null);
        return ChargingExecutionReasons.ClearanceToWaitingPoint;
    }

    /// <summary>这一轮的合格等待点里选一个；没有时答空，并答每个点为什么不合格（告警与日志用）。</summary>
    private async Task<(IdleReturnPointCandidate? Chosen, WaitingPointRegistrationVersion? Registration, string Excluded)>
        ChooseClearanceWaitingPointAsync(
            JourneyRuntimeRow runtime,
            int origin,
            RiotMapStationCatalogSnapshot currentMap,
            HashSet<int> failedBefore,
            CancellationToken cancellationToken)
    {
        RouteGraphAccess access = dispatchRound.Charging.RouteGraph;
        RouteGraphAvailability graph = access.Enabled && access.MapId == runtime.MapId
            ? await access.ReadAsync(cancellationToken).ConfigureAwait(false)
            : RouteGraphAvailability.Stale("ROUTE_GRAPH_DISABLED_OR_OTHER_MAP");
        WaitingPointRegistrationVersion? registration =
            await WaitingPointRegistry.ReadCurrentFromAsync(dbContext, cancellationToken).ConfigureAwait(false);
        if (!graph.IsUsable)
        {
            return (null, registration, IdleReturnReasons.RouteGraphUnavailable + "=" + graph.StaleReason);
        }

        IReadOnlySet<int> fixedTaskStations = WaitingPointFixedTaskStations.StationIds(
            await WaitingPointFixedTaskStations.ReadAsync(_taskTypeStations.Bindings, runtime.MapId, cancellationToken)
                .ConfigureAwait(false));
        List<string> excluded = [];
        List<IdleReturnPointCandidate> eligible = [];
        foreach (WaitingPointEntry point in (registration?.Points ?? [])
                     .Where(point => point.MapId == runtime.MapId)
                     .OrderBy(point => point.StationId))
        {
            string? why = failedBefore.Contains(point.StationId)
                ? IdleReturnReasons.PointFailedLastAttempt
                : await IdleReturnEvaluator.ExcludePointAsync(
                        registration, fixedTaskStations, currentMap, runtime.MapId, graph.Graph!, point, runtime.VehicleKey, origin,
                        async (stationId, token) => await dbContext.Set<StationExclusivityRow>().AsNoTracking()
                            .AnyAsync(row => row.MapId == runtime.MapId && row.StationId == stationId, token).ConfigureAwait(false),
                        cancellationToken)
                    .ConfigureAwait(false);
            if (why is not null)
            {
                excluded.Add($"{point.StationId}={why}");
                continue;
            }
            eligible.Add(new IdleReturnPointCandidate(point.StationId, graph.Graph!.Traverse(origin, point.StationId).TraversalCostMm));
        }
        return (
            IdleReturnEvaluator.Choose(eligible),
            registration,
            excluded.Count == 0 ? "no waiting point is registered on this Map" : string.Join(", ", excluded));
    }

    /// <summary>
    /// 一次清桩移动的推进：建单前复核、建单与对账、到点完成、失败分流。周期可能已被人工清桩结束（<paramref name="cycle"/> 为空）——
    /// 人工确认只完成清桩、不取消正在执行的移动单（票面第 7 条默认），车照常开完，等待点照常收敛。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>结果未知时全保持</b>（<c>REQ-0294</c>）：车、目标点、用途、预占、旅程都不动，单号不换，不重复建单。<b>不按超时放车放点</b>：放预占只凭证据——
    /// 从没发出过（撤回）、证明停稳没单（结束）。<b>本服务端从不取消清桩移动单</b>（白名单 1.3 只批可关联搬运需求的单与那两类充电单）：点丢了、单可能在动时只保持并告警。
    /// </para>
    /// <para>
    /// <b>FAILED</b> 与搬运、空闲返回同一条路：交故障模型（<see cref="NameStalledOrderAsync"/>）；人清除故障之后由 <c>VehicleFaultRecoveryService</c>
    /// 放预占、回到清桩中，这个周期不再自动出发。<b>被人在 RIoT 里取消或删除</b>：不重建，停稳后同样放预占、不再自动出发。
    /// </para>
    /// </remarks>
    private async Task AdvanceClearanceMoveAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow move,
        RiotMapStationCatalogSnapshot currentMap,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        JourneyStopRow charger = await dbContext.Set<JourneyStopRow>()
            .SingleAsync(row => row.JourneyId == runtime.JourneyId && row.StopRole == JourneyStopRoles.Charger, cancellationToken)
            .ConfigureAwait(false);
        OrderIntentRow intent = await dbContext.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.UpperId == move.UpperId, cancellationToken).ConfigureAwait(false);
        ChargingCycleRow? cycle = await dbContext.Set<ChargingCycleRow>()
            .SingleOrDefaultAsync(row => row.JourneyId == runtime.JourneyId && row.Phase != ChargingCyclePhases.Ended, cancellationToken)
            .ConfigureAwait(false);
        await ReplayChargingSnapshotsAsync(runtime, now, cancellationToken).ConfigureAwait(false);
        await ReplayClearanceMoveSnapshotsAsync(runtime, move, now, cancellationToken).ConfigureAwait(false);

        StationClearanceRow? clearance = cycle is null
            ? null
            : await dbContext.Set<StationClearanceRow>().AsNoTracking()
                .SingleOrDefaultAsync(row => row.CycleId == cycle.CycleId, cancellationToken).ConfigureAwait(false);
        if (cycle is { Phase: ChargingCyclePhases.Clearing } && clearance is { ConfirmedAt: not null, CompletedAt: null } &&
            await CompleteRecordedConfirmationOnTheWayAsync(runtime, charger, cycle, clearance, now, cancellationToken)
                .ConfigureAwait(false))
        {
            cycle = null;
            clearance = null;
        }
        bool clearingOpen = cycle is { Phase: ChargingCyclePhases.Clearing } && clearance is { CompletedAt: null };

        StationExclusivityRow? held = await WaitingPointExclusivity
            .HeldByAsync(dbContext, runtime.MapId, move.StationRiotId, runtime.JourneyId, cancellationToken)
            .ConfigureAwait(false);
        bool neverSent = await store.IsNeverSentAsync(intent, cancellationToken).ConfigureAwait(false);
        if (neverSent)
        {
            // Re-check the premise right before the create (REQ-0296, first branch): anything no longer true, and nothing has
            // ever gone out, so the commitment is withdrawn whole and the clearing judged afresh next round.
            (string? why, string code) = await ClearanceMoveWithdrawalAsync(runtime, move, cycle, clearingOpen, held, currentMap, cancellationToken)
                .ConfigureAwait(false);
            if (why is not null)
            {
                await EndClearanceMoveAsync(runtime, move, ClearanceMoveReleaseReasons.Withdrawn, code, cancellationToken)
                    .ConfigureAwait(false);
                LogClearanceMoveWithdrawn(logger, runtime.VehicleKey, runtime.JourneyId, why, null);
                return;
            }
        }

        if (intent is not { Status: "CONFIRMED", OrderId: not null })
        {
            MovementDispatchResult result = await movementDispatch.ReconcileOrCreateAsync(move.UpperId, cancellationToken)
                .ConfigureAwait(false);
            if (result.Outcome == MovementDispatchOutcome.Confirmed)
            {
                await SetChargingCodeAsync(runtime, ChargingExecutionReasons.ClearanceToWaitingPoint, timeProvider.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
                await PublishClearanceMoveSnapshotsAsync(runtime, charger, move, cycle, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (result.Outcome != MovementDispatchOutcome.TerminalReconciliationRequired)
            {
                await SetChargingCodeAsync(runtime, ChargingExecutionReasons.ClearanceMoveNotConfirmed, timeProvider.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
                if (result.Outcome == MovementDispatchOutcome.ResultUnknown &&
                    !await store.IsNeverSentAsync(
                            await dbContext.OrderIntents.AsNoTracking()
                                .SingleAsync(row => row.UpperId == move.UpperId, cancellationToken).ConfigureAwait(false),
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    await AbandonClearanceMoveIfItNeverAppearedAsync(runtime, move, cancellationToken).ConfigureAwait(false);
                }
                return;
            }
            intent = await dbContext.OrderIntents.AsNoTracking()
                .SingleAsync(row => row.UpperId == move.UpperId, cancellationToken).ConfigureAwait(false);
        }

        RiotOrderObservation order;
        try
        {
            order = await vehicleFacts.ReconcileByUpperIdAsync(move.UpperId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            // Unread: nothing is judged on it, and the code stays what it was.
            return;
        }

        bool absent = order.Kind == RiotOrderObservationKind.NotFound || order.IsExactAbsentAtObservation(move.UpperId);
        if (!absent)
        {
            dispatchRound.Charging.Board.SeenOrUnread(move.UpperId);
        }

        if (held is null)
        {
            // Released by hand (control-server#419) and perhaps someone else's since: never sent on to it, and never cancelled
            // here. Once the order has ended and the vehicle is proven stopped, the move ends and the clearing goes on.
            if ((order.Kind == RiotOrderObservationKind.Terminal || absent) &&
                await ProvenStoppedWithoutOrderAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false) is not null)
            {
                await EndClearanceMoveAsync(runtime, move, releaseReason: null, ChargingExecutionReasons.UnableToChargeClearing, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            await HoldClearanceMoveAsync(runtime, move, "WAITING_POINT_NO_LONGER_THIS_JOURNEYS", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (order is { Kind: RiotOrderObservationKind.Terminal, OrderState: RiotOrderState.Cancelled or RiotOrderState.Deleted })
        {
            await JudgeClearanceMoveEndedInRiotAsync(runtime, move, cycle, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (absent)
        {
            await HoldClearanceMoveAsync(runtime, move, "ORDER_NOT_FOUND_IN_RIOT", cancellationToken).ConfigureAwait(false);
            await AbandonClearanceMoveIfItNeverAppearedAsync(runtime, move, cancellationToken).ConfigureAwait(false);
            return;
        }

        bool exactOrder = order.Kind == RiotOrderObservationKind.Terminal &&
                          order.OrderState == RiotOrderState.Success &&
                          !string.IsNullOrWhiteSpace(order.OrderId) &&
                          order.OrderId == intent.OrderId &&
                          order.VehicleKey == runtime.VehicleKey &&
                          order.MapId == runtime.MapId &&
                          order.DestinationStationId == move.StationRiotId;
        if (!exactOrder)
        {
            // The same supervision as any leg: FAILED to the fault model, doors not proven locked held, HANG and SUSPENDED named.
            if (await NameStalledOrderAsync(runtime, intent, order, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
            await NameCheckpointWaitAsync(runtime, cancellationToken).ConfigureAwait(false);
            if (runtime.BlockReasonCode is null)
            {
                await SetChargingCodeAsync(runtime, ChargingExecutionReasons.ClearanceToWaitingPoint, timeProvider.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
            }
            return;
        }

        RiotVehicleObservation? vehicle = await ReadVehicleOrNullAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false);
        if (vehicle is null || !StandsStillAt(vehicle, runtime, move.StationRiotId, timeProvider.GetUtcNow()))
        {
            // The order says it succeeded, the vehicle does not yet: not complete, judged again next round, never by a timeout.
            await NameCheckpointWaitAsync(runtime, cancellationToken).ConfigureAwait(false);
            await SetChargingCodeAsync(runtime, ChargingExecutionReasons.ClearanceArrivalNotProven, timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
            if (runtime.BlockReasonSince is { } since && timeProvider.GetUtcNow() - since > runtimeOptions.OwnOrderRebuildRepeatWindow &&
                dispatchRound.Charging.Board.FirstTime("clearance-arrival-not-proven:" + runtime.JourneyId))
            {
                LogClearanceMoveHeld(logger, move.UpperId, runtime.VehicleKey, runtime.JourneyId, "ARRIVAL_NOT_PROVEN", null);
            }
            return;
        }

        await CompleteClearanceAtWaitingPointAsync(runtime, charger, move, cycle, clearingOpen ? clearance : null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 建单前复核（单从没发出过）：答撤回的理由与撤回后旅程上写的码；都成立答空理由。
    /// </summary>
    private async Task<(string? Why, string Code)> ClearanceMoveWithdrawalAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow move,
        ChargingCycleRow? cycle,
        bool clearingOpen,
        StationExclusivityRow? held,
        RiotMapStationCatalogSnapshot currentMap,
        CancellationToken cancellationToken)
    {
        if (!clearingOpen)
        {
            // Completed by a manual clearance before anything went out: the vehicle stays where it is and the journey closes.
            return ("CLEARANCE_ALREADY_COMPLETED", ChargingExecutionReasons.UnableToChargeCleared);
        }
        if (!runtimeOptions.ClearanceToWaitingPointEnabled)
        {
            return ("SWITCHED_OFF", ChargingExecutionReasons.UnableToChargeClearing);
        }
        if (held is null)
        {
            return ("WAITING_POINT_RESERVATION_GONE", ChargingExecutionReasons.UnableToChargeClearing);
        }

        RiotVehicleObservation? vehicle = await ReadVehicleOrNullAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false);
        if (vehicle is null || !StandsStillAt(vehicle, runtime, cycle!.StationId, timeProvider.GetUtcNow()) ||
            string.Equals(vehicle.BatteryState, BatteryEligibility.ChargingBatteryState, StringComparison.Ordinal))
        {
            return ("VEHICLE_NOT_STANDING_ON_ITS_CHARGER", ChargingExecutionReasons.ClearanceVehicleOffCharger);
        }

        WaitingPointRegistrationVersion? registration =
            await WaitingPointRegistry.ReadCurrentFromAsync(dbContext, cancellationToken).ConfigureAwait(false);
        IReadOnlySet<int> fixedTaskStations = WaitingPointFixedTaskStations.StationIds(
            await WaitingPointFixedTaskStations.ReadAsync(_taskTypeStations.Bindings, runtime.MapId, cancellationToken)
                .ConfigureAwait(false));
        WaitingPointEligibilityDecision decision = WaitingPointEligibility.Judge(
            registration, fixedTaskStations, currentMap, runtime.MapId, move.StationRiotId, runtime.VehicleKey);
        if (!decision.Accepts)
        {
            return ("WAITING_POINT_NO_LONGER_ELIGIBLE:" + decision.Reason, ChargingExecutionReasons.UnableToChargeClearing);
        }

        IReadOnlyList<string> gaps = await IdleReturnDepartureGapsAsync(runtime, cancellationToken).ConfigureAwait(false);
        return gaps.Count > 0
            ? ("DEPARTURE_NOT_PROVEN:" + string.Join(", ", gaps), ChargingExecutionReasons.ClearanceDepartureNotProven)
            : (null, "");
    }

    /// <summary>
    /// 移动途中人工确认已记下、清桩还没完成（确认那一刻旧单还没收敛，之后才终结）：与清桩中那一支同一个完成（人工证明），只在车已不在原桩上时；
    /// 移动照常继续。答是否这一次完成了。
    /// </summary>
    private async Task<bool> CompleteRecordedConfirmationOnTheWayAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow charger,
        ChargingCycleRow cycle,
        StationClearanceRow clearance,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RiotOrderObservation oldOrder;
        try
        {
            oldOrder = await vehicleFacts.ReconcileByUpperIdAsync(charger.UpperId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        string disposition = ManualStationClearance.Disposition(oldOrder, charger.UpperId);
        RiotVehicleObservation? vehicle = await ReadVehicleOrNullAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false);
        if (!ManualStationClearance.Settled(disposition) ||
            ManualStationClearance.StillOnTheCharger(vehicle, cycle.StationId, runtimeOptions.MapIdentity) ||
            !await ChargerClearanceRelease.CompleteAndReleaseAsync(
                    dbContext, clearance.ClearanceId, cycle.CycleId, cycle.Version, ChargingExecutionReasons.UnableToChargeCleared,
                    disposition, now, cancellationToken)
                .ConfigureAwait(false))
        {
            return false;
        }
        LogClearedChargerReleased(logger, runtime.JourneyId, runtime.VehicleKey, charger.StationRiotId, disposition, null);
        return true;
    }

    /// <summary>
    /// 清桩移动单在 RIoT 里被取消或删除：不重建。车证明停稳、没有活动订单之后，不在那个等待点上就放预占（<see cref="ClearanceMoveReleaseReasons.Ended"/>），
    /// 这个周期不再自动出发；已经停在那个点上，就当作到了却没有订单那一半的证据——不完成清桩，保持并告警，人工清桩之后再收敛到那个点上。
    /// 证明不了停稳时保持一切。
    /// </summary>
    private async Task JudgeClearanceMoveEndedInRiotAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow move,
        ChargingCycleRow? cycle,
        CancellationToken cancellationToken)
    {
        if (await ProvenStoppedWithoutOrderAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false) is null)
        {
            await HoldClearanceMoveAsync(runtime, move, "ORDER_ENDED_IN_RIOT_STOP_NOT_PROVEN", cancellationToken).ConfigureAwait(false);
            return;
        }

        RiotVehicleObservation? vehicle = await ReadVehicleOrNullAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false);
        if (vehicle is { CurrentStationId: int at } && at == move.StationRiotId)
        {
            if (cycle is null)
            {
                // The clearance was completed by a person meanwhile: the vehicle stands on its point, which it keeps.
                JourneyStopRow charger = await dbContext.Set<JourneyStopRow>()
                    .SingleAsync(row => row.JourneyId == runtime.JourneyId && row.StopRole == JourneyStopRoles.Charger, cancellationToken)
                    .ConfigureAwait(false);
                await CompleteClearanceAtWaitingPointAsync(runtime, charger, move, cycle: null, clearance: null, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            await HoldClearanceMoveAsync(runtime, move, "ORDER_ENDED_IN_RIOT_ON_THE_WAITING_POINT", cancellationToken).ConfigureAwait(false);
            return;
        }

        string code = cycle is null ? ChargingExecutionReasons.UnableToChargeCleared : ChargingExecutionReasons.ClearanceMoveEnded;
        await EndClearanceMoveAsync(runtime, move, ClearanceMoveReleaseReasons.Ended, code, cancellationToken).ConfigureAwait(false);
        if (dispatchRound.Charging.Board.FirstTime("clearance-move-ended:" + runtime.JourneyId))
        {
            LogClearanceMoveEnded(logger, move.UpperId, runtime.VehicleKey, runtime.JourneyId, null);
        }
    }

    /// <summary>
    /// 建单发出过、之后 RIoT 持续答查无此单：与充电单放弃同一组证据（连续满 <c>JourneyRuntime:ChargingOrderAbsentAbandonAfter</c>、车证明停稳没有任务号、
    /// 未完成订单清单读全且没有这辆车的单）全部成立，才按「证明没单没动」放预占、排除这个点、回到清桩中重评（<c>REQ-0296</c>）。
    /// </summary>
    private async Task AbandonClearanceMoveIfItNeverAppearedAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow move,
        CancellationToken cancellationToken)
    {
        ChargingAllocationBoard board = dispatchRound.Charging.Board;
        RiotOrderObservation observed;
        try
        {
            observed = await vehicleFacts.ReconcileByUpperIdAsync(move.UpperId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            board.SeenOrUnread(move.UpperId);
            return;
        }
        if (observed.Kind != RiotOrderObservationKind.NotFound && !observed.IsExactAbsentAtObservation(move.UpperId))
        {
            board.SeenOrUnread(move.UpperId);
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (now - board.AbsentSince(move.UpperId, now) < runtimeOptions.ChargingOrderAbsentAbandonAfter ||
            await ProvenStoppedWithoutOrderAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false) is null)
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

        bool clearing = await dbContext.Set<ChargingCycleRow>().AsNoTracking()
            .AnyAsync(row => row.JourneyId == runtime.JourneyId && row.Phase == ChargingCyclePhases.Clearing, cancellationToken)
            .ConfigureAwait(false);
        await EndClearanceMoveAsync(
                runtime, move, ClearanceMoveReleaseReasons.NeverAppeared,
                clearing ? ChargingExecutionReasons.UnableToChargeClearing : ChargingExecutionReasons.UnableToChargeCleared,
                cancellationToken)
            .ConfigureAwait(false);
        board.SeenOrUnread(move.UpperId);
        LogClearanceMoveHeld(logger, move.UpperId, runtime.VehicleKey, runtime.JourneyId, "ORDER_NEVER_APPEARED_ABANDONED", null);
    }

    /// <summary>到点：清桩（还没完成时）、桩释放、等待点转占用、用途放开、旅程收尾，一个事务。</summary>
    /// <remarks>
    /// <para>
    /// <b>同一个事务</b>（票面崩溃点）：中间崩掉什么也不留——不会有「桩已空而清桩未完成」，也不会有「清桩已完成而等待点仍是预占」；下一轮按同一组事实再判。
    /// </para>
    /// <para>
    /// <b>执行前重判前提</b>：清桩完成按「还没完成」更新、周期按读到的版本结束、桩按读到的持有者释放、等待点按这一趟持有；任一不再成立整笔回滚。
    /// 人工清桩在同一刻完成过的，这一次只做等待点与旅程那一半——不重复释放、不重复写记录。
    /// </para>
    /// </remarks>
    private async Task CompleteClearanceAtWaitingPointAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow charger,
        JourneyStopRow move,
        ChargingCycleRow? cycle,
        StationClearanceRow? clearance,
        CancellationToken cancellationToken)
    {
        string? disposition = null;
        if (cycle is not null && clearance is not null)
        {
            RiotOrderObservation oldOrder;
            try
            {
                oldOrder = await vehicleFacts.ReconcileByUpperIdAsync(charger.UpperId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                          !cancellationToken.IsCancellationRequested)
            {
                oldOrder = new RiotOrderObservation(charger.UpperId, RiotOrderObservationKind.Unknown, null);
            }
            disposition = ManualStationClearance.Disposition(oldOrder, charger.UpperId);
            if (!ManualStationClearance.Settled(disposition))
            {
                // It was settled when the move was committed; an old order RIoT now reads otherwise completes nothing.
                await HoldClearanceMoveAsync(runtime, move, "OLD_ORDER_NOT_READ_ENDED:" + disposition, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        string ending;
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            if (cycle is not null && clearance is not null)
            {
                if (!await ClearanceAtWaitingPointCompletion.CompleteAsync(
                        dbContext, clearance.ClearanceId, cycle.CycleId, cycle.Version, disposition!, runtime.MapId, move.StationRiotId, now,
                        cancellationToken).ConfigureAwait(false))
                {
                    // Completed by a person this very moment, or the cycle moved on: nothing of this attempt stands; next round.
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                ending = ChargingExecutionReasons.UnableToChargeClearedAtWaitingPoint;
            }
            else
            {
                ending = (await dbContext.Set<ChargingCycleRow>().AsNoTracking()
                              .Where(row => row.JourneyId == runtime.JourneyId)
                              .Select(row => row.EndReason)
                              .ToArrayAsync(cancellationToken).ConfigureAwait(false))
                          .SingleOrDefault(reason => reason is not null && ChargingExecutionReasons.ClearedEndings.Contains(reason))
                      ?? ChargingExecutionReasons.UnableToChargeCleared;
            }

            StationExclusivityRow? held = await WaitingPointExclusivity
                .HeldByAsync(dbContext, runtime.MapId, move.StationRiotId, runtime.JourneyId, cancellationToken)
                .ConfigureAwait(false);
            if (held is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            await WaitingPointExclusivity.StageOccupyAsync(dbContext, held, now, cancellationToken).ConfigureAwait(false);
            move.Status = JourneyStopStatuses.Completed;
            charger.Status = JourneyStopStatuses.Completed;
            checkpointWaits.Clear(runtime.VehicleKey);
            await JourneyPurposeClaimRelease.StageAsync(dbContext, runtime.JourneyId, now, ending, cancellationToken).ConfigureAwait(false);
            foreach (string superseded in ChargingSnapshotIds(runtime).Concat(ClearingSnapshotIds(runtime))
                         .Concat(ClearanceMoveShape.SnapshotIds(runtime.JourneyId, ClearanceMoveShape.AttemptOf(move))))
            {
                await FenceSupersededSnapshotAsync(superseded, cancellationToken).ConfigureAwait(false);
            }
            await JourneyClosure.StageClearanceAtWaitingPointAsync(dbContext, runtime, move, ending, now, cancellationToken)
                .ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        dispatchRound.Charging.Board.Unsay("clearing-old-order-unsettled:" + runtime.JourneyId);
        LogClearedAtWaitingPoint(logger, runtime.JourneyId, runtime.VehicleKey, move.StationRiotId, null);
        await JourneyClosure.SendAsync(publisher, dbContext, runtime.AgvId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>一次清桩移动结束（没到点）：<see cref="ClearanceMoveEnding"/> 暂存，写码，一次保存，然后发「回到清桩中」的那两张快照。</summary>
    private async Task EndClearanceMoveAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow move,
        string? releaseReason,
        string code,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        checkpointWaits.Clear(runtime.VehicleKey);
        await ClearanceMoveEnding.StageAsync(dbContext, runtime, move, releaseReason, now, cancellationToken).ConfigureAwait(false);
        runtime.SetBlockReason(code, now);
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        int attempt = ClearanceMoveShape.AttemptOf(move);
        await SendIdleReturnSnapshotAsync(ClearanceMoveShape.BackPlanMessageId(runtime.JourneyId, attempt), cancellationToken)
            .ConfigureAwait(false);
        await SendIdleReturnSnapshotAsync(ClearanceMoveShape.BackStateMessageId(runtime.JourneyId, attempt), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HoldClearanceMoveAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow move,
        string why,
        CancellationToken cancellationToken)
    {
        await SetChargingCodeAsync(runtime, ChargingExecutionReasons.ClearanceMoveHeld, timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        if (dispatchRound.Charging.Board.FirstTime("clearance-move-held:" + runtime.JourneyId))
        {
            LogClearanceMoveHeld(logger, move.UpperId, runtime.VehicleKey, runtime.JourneyId, why, null);
        }
    }

    /// <summary>
    /// 单确认之后，把途中的计划（原桩腿 <c>COMPLETED</c>、等待点腿 <c>ACTIVE</c>）与业务状态（仍是 <c>CLEARING_MAINTENANCE</c>）暂存、保存、发出。
    /// 被取代、还没被确认的清桩中快照同一次保存里退役。id 由旅程与尝试序号派生，重跑与补发都是同一张。
    /// </summary>
    private async Task PublishClearanceMoveSnapshotsAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow charger,
        JourneyStopRow move,
        ChargingCycleRow? cycle,
        CancellationToken cancellationToken)
    {
        SessionRecoveryRow? session = await dbContext.SessionRecoveries.AsNoTracking()
            .SingleOrDefaultAsync(row => row.AgvId == runtime.AgvId, cancellationToken).ConfigureAwait(false);
        int attempt = ClearanceMoveShape.AttemptOf(move);
        string planId = ClearanceMoveShape.PlanMessageId(runtime.JourneyId, attempt);
        string stateId = ClearanceMoveShape.StateMessageId(runtime.JourneyId, attempt);
        if (session is null ||
            await dbContext.ProtocolOutbox.AsNoTracking()
                .AnyAsync(row => row.MessageId == planId || row.MessageId == stateId, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach (string superseded in ClearingSnapshotIds(runtime)
                     .Concat(ClearanceMoveShape.SnapshotIds(runtime.JourneyId, attempt - 1)))
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
            new UpcomingStopPlanProjection(
                planRevision,
                [
                    JourneyPlanBuilder.ChargerLeg(runtime, charger, "COMPLETED"),
                    JourneyPlanBuilder.IdleReturnLeg(runtime, move, arrived: false),
                ]),
            now,
            cancellationToken).ConfigureAwait(false);
        await OnboardJourneyPublisher.StageVehicleBusinessStateAsync(
            store,
            stateId,
            runtime.AgvId,
            session.SessionGeneration,
            new VehicleBusinessProjection(
                stateRevision, "READY", cycle is null ? null : VehicleActivePurposes.ClearingMaintenance, false,
                PublishedBatteryState(runtime), cycle?.WireState ?? ChargingCycleWireStates.NotCharging, null, []),
            // A millisecond after the plan, so a replay -- which sends in creation order -- sends the plan first too.
            now.AddMilliseconds(1),
            cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await SendIdleReturnSnapshotAsync(planId, cancellationToken).ConfigureAwait(false);
        await SendIdleReturnSnapshotAsync(stateId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>车重连之后，把这一趟清桩移动没被确认的快照按原 id 补发，不产生第二份。</summary>
    private async Task ReplayClearanceMoveSnapshotsAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow move,
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
                    new HashSet<string>(
                        ClearanceMoveShape.SnapshotIds(runtime.JourneyId, ClearanceMoveShape.AttemptOf(move)), StringComparer.Ordinal),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // Not on the line this moment; the next round, or the next reconnect, sends them.
        }
    }

    private async Task<RiotVehicleObservation?> ReadVehicleOrNullAsync(string vehicleKey, CancellationToken cancellationToken)
    {
        try
        {
            return await vehicleFacts.ReadVehicleAsync(vehicleKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>这一轮没出发的理由，理由变了才记一条（每趟旅程）。</summary>
    private void NotStarted(JourneyRuntimeRow runtime, string why)
    {
        if (dispatchRound.Charging.Board.FirstTime($"clearance-not-started:{runtime.JourneyId}:{why}"))
        {
            LogClearanceMoveNotStarted(logger, runtime.VehicleKey, runtime.JourneyId, why, null);
        }
    }
}
