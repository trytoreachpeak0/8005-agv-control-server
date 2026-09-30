using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

// 批次8-19（control-server#390）：空闲返回的执行。承诺（用途占有 + 等待点预占）由批次8-18 的评估器在派车轮末尾形成；这里从承诺物化旅程、
// 过出发前安全门、建单与对账、判到点、收敛、失败分流。离点释放在每轮开头的离点清扫里（FixedStationExclusivitySweep）。
public sealed partial class JourneyRuntimeEngine
{
    private static readonly Action<ILogger, string, string, int, Exception?> LogIdleReturnMaterialized =
        LoggerMessage.Define<string, string, int>(
            LogLevel.Information,
            new EventId(2220, nameof(LogIdleReturnMaterialized)),
            "Idle return {JourneyId} materialized for vehicle {VehicleKey}: one move to waiting point {StationId}.");

    private static readonly Action<ILogger, string, string, string, Exception?> LogIdleReturnOrphaned =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2221, nameof(LogIdleReturnOrphaned)),
            "Idle return {JourneyId} of vehicle {VehicleKey} cannot be materialized ({Why}); its commitment is released and the " +
            "vehicle is judged again.");

    private static readonly Action<ILogger, string, Exception?> LogIdleReturnMaterializationFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2222, nameof(LogIdleReturnMaterializationFailed)),
            "Idle return {JourneyId} could not be materialized this round; nothing of it was written and the next round tries again.");

    private static readonly Action<ILogger, string, string, string, Exception?> LogIdleReturnDepartureNotProven =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(2223, nameof(LogIdleReturnDepartureNotProven)),
            "Idle return {JourneyId} of vehicle {VehicleKey} is not sent off: the pre-departure safety gate is not met ({Gaps}). " +
            "No order is created; the commitment stays and the gate is asked again next round.");

    private static readonly Action<ILogger, string, string, string, int, Exception?> LogIdleReturnEnded =
        LoggerMessage.Define<string, string, string, int>(
            LogLevel.Warning,
            new EventId(2224, nameof(LogIdleReturnEnded)),
            "Idle return {JourneyId} of vehicle {VehicleKey} ended with {ReasonCode} (waiting point {StationId}); its purpose claim " +
            "is released and the vehicle is judged again.");

    private static readonly Action<ILogger, string, string, int, Exception?> LogIdleReturnConverged =
        LoggerMessage.Define<string, string, int>(
            LogLevel.Information,
            new EventId(2225, nameof(LogIdleReturnConverged)),
            "Idle return {JourneyId}: vehicle {VehicleKey} arrived at waiting point {StationId}; the reservation is now an " +
            "occupancy and the IDLE_RETURN purpose is released. The occupancy is released only on departure evidence.");

    private static readonly Action<ILogger, string, string, string, Exception?> LogIdleReturnHeld =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2226, nameof(LogIdleReturnHeld)),
            "{ReasonCode}: idle return {JourneyId} of vehicle {VehicleKey} is held with its commitment and waiting point; nothing " +
            "is released until the vehicle is proven stopped with no order.");

    /// <summary>
    /// 物化：有 <c>IDLE_RETURN</c> 用途占有、却还没有那个 <c>JourneyId</c> 的旅程行的承诺，建旅程行、开往等待点的停靠与订单意图，按
    /// <c>JourneyId</c> 幂等（<see cref="WireToGateStore.MaterializeIdleReturnAsync"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么与承诺不在同一次保存</b>（方案 X，调度 2026-09-29 定）：承诺由派车轮末尾的评估器在一次保存里取得用途占有与预占；旅程行
    /// 与意图在下一轮这里补。中间崩溃、重启，下一轮照样补建恰好一次：物化读不到旅程行才写，写的时候旅程表主键再兜一次。
    /// </para>
    /// <para>
    /// <b>孤儿承诺不锁死车</b>（准入线第 3 条）：物化不出旅程的承诺——车不在名册、预占行已不在（人工释放，control-server#419）、
    /// 等待点已不在登记里——整个释放（用途占有与还在的预占同一次保存），车回到下一轮评估。这也覆盖本票合入前留下的存量承诺
    /// （合成 L2 在批次8-18 下打开开关时留下的那种）：它们照样被物化或释放，不需要改库。
    /// </para>
    /// <para>
    /// 等待点是否仍然合格（停用、白名单、实时目录）不在这里判，在建单之前判（<see cref="IdleReturnPointIneligibleAsync"/>）：
    /// 那一刻判的结果才决定这辆车动不动，物化只要求建得出这几行。
    /// </para>
    /// <para>
    /// 开关关着也照做：关掉只挡新承诺，不取消、不改写既有的（<c>REQ-0291</c>）。
    /// </para>
    /// </remarks>
    private async Task MaterializeIdleReturnsAsync(CancellationToken cancellationToken)
    {
        VehiclePurposeClaimRow[] claims = await dbContext.Set<VehiclePurposeClaimRow>().AsNoTracking()
            .Where(row => row.Purpose == VehiclePurposes.IdleReturn)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (VehiclePurposeClaimRow claim in claims)
        {
            if (await dbContext.JourneyRuntimes.AsNoTracking()
                    .AnyAsync(row => row.JourneyId == claim.JourneyId, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            try
            {
                await MaterializeIdleReturnAsync(claim, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Nothing this round has tracked yet is live (the rounds's journeys are read after this), so what the failed
                // attempt staged is dropped wholesale rather than left for a later save to write half of.
                dbContext.ChangeTracker.Clear();
                LogIdleReturnMaterializationFailed(logger, claim.JourneyId, error);
            }
        }
    }

    private async Task MaterializeIdleReturnAsync(VehiclePurposeClaimRow claim, CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        FleetVehicle? vehicle = roster.Vehicles.SingleOrDefault(
            item => string.Equals(item.VehicleKey, claim.VehicleKey, StringComparison.Ordinal));
        StationExclusivityRow? station = await dbContext.Set<StationExclusivityRow>()
            .SingleOrDefaultAsync(
                row => row.JourneyId == claim.JourneyId && row.StationKind == StationExclusivityKinds.WaitingPoint,
                cancellationToken).ConfigureAwait(false);
        WaitingPointRegistrationVersion? registration =
            await WaitingPointRegistry.ReadCurrentFromAsync(dbContext, cancellationToken).ConfigureAwait(false);
        WaitingPointEntry? point = station is null
            ? null
            : registration?.Points.SingleOrDefault(item => item.MapId == station.MapId && item.StationId == station.StationId);
        string? orphaned = vehicle is null ? "VEHICLE_NOT_IN_ROSTER"
            : station is null ? "WAITING_POINT_RESERVATION_GONE"
            : station.MapId != runtimeOptions.MapId ? "WAITING_POINT_ON_ANOTHER_MAP"
            : point is null ? WaitingPointEligibilityReasons.NotRegistered
            : null;
        if (orphaned is not null)
        {
            await JourneyPurposeClaimRelease.StageAsync(
                    dbContext, claim.JourneyId, now, IdleReturnExecutionReasons.CommitmentOrphaned, cancellationToken)
                .ConfigureAwait(false);
            if (station is not null)
            {
                await WaitingPointExclusivity.StageReleaseAsync(
                        dbContext, station, now, IdleReturnExecutionReasons.CommitmentOrphaned, cancellationToken)
                    .ConfigureAwait(false);
            }
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            LogIdleReturnOrphaned(logger, claim.JourneyId, claim.VehicleKey, orphaned, null);
            return;
        }

        (JourneyRuntimeRow runtime, JourneyStopRow stop, OrderIntent intent) = IdleReturnJourneyShape.Build(
            claim.JourneyId, vehicle!, station!.StationId, point!.StationName, runtimeOptions, now);
        dbContext.Entry(station).State = EntityState.Detached;
        if (await store.MaterializeIdleReturnAsync(runtime, stop, intent, cancellationToken).ConfigureAwait(false))
        {
            LogIdleReturnMaterialized(logger, runtime.JourneyId, runtime.VehicleKey, stop.StationRiotId, null);
        }
    }

    /// <summary>
    /// 空闲返回的推进：不进搬运状态机（没有清单、录入、装卸），只走这一段——建单前的核验与安全门、建单与对账、到点、收敛、失败分流。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>不看车载端会话就走。</b>到点与失败的证据都是 RIoT 的（<c>REQ-0295</c> 的组合里没有车载端那一项）：车载端对等待点没有业务动作，
    /// 真车载端又在本服务端的单在途时整段不就绪（control-server#314），拿它当到点证据会让车永远到不了。车载端只在两处出现：
    /// 建单前的安全门（它的安全状态），与发给它的计划和业务状态（有会话就发，没连上就等重连补发）。
    /// </para>
    /// <para>
    /// <b>结果未知时五样全保持</b>（<c>REQ-0294</c>）：车辆、目标点、用途占有、等待点预占都不动，单号不换（意图按 <c>upperId</c> 复用，
    /// <see cref="MovementDispatchService.ReconcileOrCreateAsync"/> 对建过的单只对账不再建），也不发「已出发」——途中的计划与
    /// <c>IDLE_RETURN</c> 只在单确认之后才发。
    /// </para>
    /// <para>
    /// <b>不按超时释放。</b>到点证据缺哪一样都只是这一轮不收敛，下一轮再判；这里没有一个会因为等久了而放车或放点的分支。
    /// </para>
    /// </remarks>
    private async Task AdvanceIdleReturnAsync(
        JourneyRuntimeRow runtime,
        RiotMapStationCatalogSnapshot currentMap,
        CancellationToken cancellationToken)
    {
        if (runtime.Stage != JourneyRuntimeStage.AwaitingPickupArrival)
        {
            // An idle return is never blocked by this engine: it stays in its travelling stage with a code, like a transport
            // leg whose order stalled. Any other stage was written by something that does not know idle returns.
            throw new InvalidDataException(
                $"Idle return {runtime.JourneyId} is in stage {runtime.Stage}; only {JourneyRuntimeStage.AwaitingPickupArrival} is advanced.");
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        JourneyStopRow stop = await dbContext.Set<JourneyStopRow>()
            .SingleAsync(row => row.JourneyId == runtime.JourneyId, cancellationToken).ConfigureAwait(false);
        OrderIntentRow intent = await dbContext.OrderIntents.AsNoTracking()
            .SingleAsync(row => row.UpperId == stop.UpperId, cancellationToken).ConfigureAwait(false);
        bool neverSent = await store.IsNeverSentAsync(intent, cancellationToken).ConfigureAwait(false);
        await ReplayIdleReturnSnapshotsAsync(runtime, now, cancellationToken).ConfigureAwait(false);

        // The reservation is this journey's, or the commitment is void (control-server#419 released it by hand, and another
        // vehicle may have taken it since): the vehicle must not be sent to, or go on to, a point it does not hold.
        StationExclusivityRow? held = await WaitingPointExclusivity
            .HeldByAsync(dbContext, runtime.MapId, stop.StationRiotId, runtime.JourneyId, cancellationToken)
            .ConfigureAwait(false);
        if (held is null)
        {
            await HandleLostWaitingPointAsync(runtime, stop, intent, neverSent, now, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (neverSent)
        {
            // REQ-0296, first branch: the point is judged again before the first create. No longer eligible, and nothing was
            // ever sent: the purpose claim and the reservation go in one save, and the vehicle is judged afresh.
            if (await IdleReturnPointIneligibleAsync(runtime, stop, currentMap, cancellationToken).ConfigureAwait(false)
                is { } ineligible)
            {
                await EndIdleReturnAsync(
                        runtime, stop, IdleReturnExecutionReasons.WaitingPointNoLongerEligible, stillAtWaitingPoint: false,
                        releaseStationNow: held, now, cancellationToken, detail: ineligible)
                    .ConfigureAwait(false);
                return;
            }

            IReadOnlyList<string> gaps = await IdleReturnDepartureGapsAsync(runtime, cancellationToken).ConfigureAwait(false);
            if (gaps.Count > 0)
            {
                if (!string.Equals(runtime.BlockReasonCode, IdleReturnExecutionReasons.DepartureNotProven, StringComparison.Ordinal))
                {
                    LogIdleReturnDepartureNotProven(logger, runtime.JourneyId, runtime.VehicleKey, string.Join(", ", gaps), null);
                }
                runtime.SetBlockReason(IdleReturnExecutionReasons.DepartureNotProven, now);
                runtime.UpdatedAt = now;
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (intent is not { Status: "CONFIRMED", OrderId: not null })
        {
            MovementDispatchResult result = await movementDispatch.ReconcileOrCreateAsync(stop.UpperId, cancellationToken)
                .ConfigureAwait(false);
            if (result.Outcome == MovementDispatchOutcome.Confirmed)
            {
                runtime.SetBlockReason(null, timeProvider.GetUtcNow());
                runtime.UpdatedAt = timeProvider.GetUtcNow();
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await PublishIdleReturnSnapshotsAsync(runtime, stop, cancellationToken).ConfigureAwait(false);
            }
            else if (result.Outcome != MovementDispatchOutcome.TerminalReconciliationRequired)
            {
                // Result unknown, create gate closed, and the like: everything stays, the same upperId is reconciled next round.
                runtime.SetBlockReason($"{IdleReturnExecutionReasons.LegName}_{result.Outcome}", timeProvider.GetUtcNow());
                runtime.UpdatedAt = timeProvider.GetUtcNow();
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            // TerminalReconciliationRequired: the order ended before its create was confirmed. Judged below exactly like a
            // confirmed order that ended (control-server#367's reading).
            intent = await dbContext.OrderIntents.AsNoTracking()
                .SingleAsync(row => row.UpperId == stop.UpperId, cancellationToken).ConfigureAwait(false);
        }

        RiotOrderObservation order = await vehicleFacts.ReconcileByUpperIdAsync(stop.UpperId, cancellationToken)
            .ConfigureAwait(false);
        if (order is { Kind: RiotOrderObservationKind.Terminal, OrderState: RiotOrderState.Cancelled or RiotOrderState.Deleted })
        {
            await JudgeEndedIdleReturnOrderAsync(
                    runtime, stop, IdleReturnExecutionReasons.OrderEnded, IdleReturnExecutionReasons.OrderEndedStopNotProven,
                    now, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        bool exactOrder = order.Kind == RiotOrderObservationKind.Terminal &&
                          order.OrderState == RiotOrderState.Success &&
                          !string.IsNullOrWhiteSpace(order.OrderId) &&
                          order.OrderId == intent.OrderId &&
                          order.VehicleKey == runtime.VehicleKey &&
                          order.MapId == runtime.MapId &&
                          order.DestinationStationId == stop.StationRiotId;
        if (!exactOrder)
        {
            // The same supervision as a transport leg (REQ-0232, REQ-0246): FAILED goes to the fault model, doors not proven
            // locked on the move are held, HANG and SUSPENDED are named. CANCELLED and DELETED never get here (above), so no
            // rebuild is recorded for an idle return.
            if (await NameStalledOrderAsync(runtime, intent, order, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
            await NameCheckpointWaitAsync(runtime, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!await ArrivedAtWaitingPointAsync(runtime, stop, cancellationToken).ConfigureAwait(false))
        {
            // The order says it succeeded, the vehicle does not yet: in transit, reconciled again next round. No timeout.
            await NameCheckpointWaitAsync(runtime, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ConvergeIdleReturnAsync(runtime, stop, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 到点证据的车辆那一半（<c>REQ-0295</c>；订单那一半——本次 <c>upperId</c> 的单达到 <c>SUCCESS</c>、订单号、车、图、目标站都与预占的
    /// 等待点一致——调用方已判）：车在线、启用、空闲、当前图加当前站精确等于这个等待点、速度为零、无锁、无在执行的任务，读数新鲜。
    /// </summary>
    /// <remarks>
    /// 与搬运到站（<see cref="CheckArrivalAsync"/>）同一组 RIoT 事实，少了车载端那一组（仓位锁闭、开锁输出复位、车载端报停稳）：
    /// 等待点上没有装卸，车载端对它没有业务动作；它的安全状态在出发之前已由安全门看过。
    /// </remarks>
    private async Task<bool> ArrivedAtWaitingPointAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        CancellationToken cancellationToken)
    {
        RiotVehicleObservation vehicle = await vehicleFacts.ReadVehicleAsync(runtime.VehicleKey, cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        return vehicle.Connected && vehicle.Enabled &&
               vehicle.ProcState == "IDLE" &&
               vehicle.CurrentMap == runtime.MapIdentity &&
               vehicle.CurrentStationId == stop.StationRiotId &&
               vehicle.Speed == 0 &&
               vehicle.LockStatus == 0 &&
               string.IsNullOrWhiteSpace(vehicle.OrderTaskId) &&
               vehicle.ObservedAt <= now && now - vehicle.ObservedAt <= runtimeOptions.MaximumEvidenceAge;
    }

    /// <summary>
    /// 收敛（<c>REQ-0293</c>）：预占转占用、用途占有释放、旅程收尾，同一次保存。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>「完整收敛」在这台服务器上的定义</b>：到点证据全部满足（订单与车辆两半），这一趟的等待点独占此刻仍是它的，转成占用，
    /// 撤下 <c>IDLE_RETURN</c> 的业务状态与 <c>ARRIVED</c> 的等待点腿计划已进发件箱——这几样与用途释放在同一次保存里。
    /// <b>不等车载端确认那两张快照</b>：等确认会把车的可选择与车载端的链路绑在一起，一台车载端没连上的车会一直占着 <c>IDLE_RETURN</c>、
    /// 接不了搬运；那两张在发件箱里，车重连后由收尾补发送到（<see cref="JourneyClosure.ReplayIdsAsync"/>）。
    /// </para>
    /// <para>
    /// <b>同一次保存</b>，所以不存在「用途已放、独占仍是预占」的中间态：崩在保存之前，下一轮从头再判一次；崩在之后，一切都已落库。
    /// </para>
    /// <para>
    /// <b>独占已不是它的</b>（被人工释放、之后被别的车取得，control-server#419）：不转占用，按承诺作废收尾，告警。车已经停在那个点上，
    /// 那个点却归了别人——服务端撤不回一辆已经停下的车，能做的是不再说它占着那里、并让人知道。
    /// </para>
    /// </remarks>
    private async Task ConvergeIdleReturnAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        StationExclusivityRow? held = await WaitingPointExclusivity
            .HeldByAsync(dbContext, runtime.MapId, stop.StationRiotId, runtime.JourneyId, cancellationToken)
            .ConfigureAwait(false);
        if (held is null)
        {
            await EndIdleReturnAsync(
                    runtime, stop, IdleReturnExecutionReasons.WaitingPointLostAtArrival, stillAtWaitingPoint: false,
                    releaseStationNow: null, now, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await WaitingPointExclusivity.StageOccupyAsync(dbContext, held, now, cancellationToken).ConfigureAwait(false);
        checkpointWaits.Clear(runtime.VehicleKey);
        await IdleReturnEnding.StageAsync(
                dbContext, runtime, stop, reasonCode: null, IdleReturnExecutionReasons.ConvergedAtWaitingPoint,
                stillAtWaitingPoint: true, releaseStationNow: null, now, cancellationToken)
            .ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogIdleReturnConverged(logger, runtime.JourneyId, runtime.VehicleKey, stop.StationRiotId, null);
        await JourneyClosure.SendAsync(publisher, dbContext, runtime.AgvId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 等待点独占已不是这一趟的（<c>held</c> 为空）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 单从没发出过：作废承诺、释放用途占有，车不出发。
    /// </para>
    /// <para>
    /// 单可能在 RIoT 上：车不得继续开往那个点，所以对这张单发一次取消（经 <see cref="RiotOrderCommandService"/>，有审计；发过就不再发），
    /// 然后与单被别人取消同样处理——RIoT 报单终结、车证明停稳、没有活动订单，才收尾；在那之前承诺与用途占有都保持
    /// （<c>REQ-0296</c>：订单存在、车可能在动时保持）。单 FAILED 走故障模型，与搬运一样。
    /// </para>
    /// </remarks>
    private async Task HandleLostWaitingPointAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        OrderIntentRow intent,
        bool neverSent,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (neverSent)
        {
            await EndIdleReturnAsync(
                    runtime, stop, IdleReturnExecutionReasons.WaitingPointLost, stillAtWaitingPoint: false,
                    releaseStationNow: null, now, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        RiotOrderObservation order = await vehicleFacts.ReconcileByUpperIdAsync(stop.UpperId, cancellationToken)
            .ConfigureAwait(false);
        if (order is { Kind: RiotOrderObservationKind.Terminal, OrderState: RiotOrderState.Failed } && intent.OrderId is not null)
        {
            await NameStalledOrderAsync(runtime, intent, order, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Arrived at a point that is no longer this journey's: the arrival does not convert anything (control-server#419 review).
        if (order is { Kind: RiotOrderObservationKind.Terminal, OrderState: RiotOrderState.Success } &&
            await ArrivedAtWaitingPointAsync(runtime, stop, cancellationToken).ConfigureAwait(false))
        {
            await EndIdleReturnAsync(
                    runtime, stop, IdleReturnExecutionReasons.WaitingPointLostAtArrival, stillAtWaitingPoint: false,
                    releaseStationNow: null, now, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (order.Kind == RiotOrderObservationKind.Active && order.OrderId is string orderId && orderCommands is not null &&
            !await OwnCancelIssuedAsync(orderId, cancellationToken).ConfigureAwait(false))
        {
            await orderCommands.IssueAsync(
                    RiotOrderCommandKind.Cancel,
                    new RiotOrderCommandTarget(runtime.AgvId, stop.UpperId, orderId),
                    "control-server#390: the idle return's waiting point is no longer this vehicle's",
                    faultGeneration: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (order.Kind is RiotOrderObservationKind.Terminal or RiotOrderObservationKind.NotFound)
        {
            await JudgeEndedIdleReturnOrderAsync(
                    runtime, stop, IdleReturnExecutionReasons.WaitingPointLost,
                    IdleReturnExecutionReasons.WaitingPointLostOrderInFlight, now, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await HoldIdleReturnAsync(runtime, IdleReturnExecutionReasons.WaitingPointLostOrderInFlight, now, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 单已不在执行（在 RIoT 被取消或删除，或为作废承诺而由本服务端取消）：车证明停稳、没有活动订单，才按已确认失败收尾；
    /// 否则保持承诺与独占、写 <paramref name="holdCode"/>（<c>REQ-0296</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不按 <c>REQ-0360</c> 同车重建</b>（票面第 7 条，开工核实）：<c>REQ-0360</c> 的对象是承载需求的在途单——重建「继续承载原订单中
    /// 尚未终止的 DemandId」，<c>REQ-0361</c> 的重复即停也按 <c>DemandId</c> 计。空闲返回单没有需求，两条都无处落脚；它被取消是
    /// <c>REQ-0296</c> 所说的失败：可能已有订单或仍在动时保持，已证明没单没动时释放并在排除原失败点后重评。
    /// </para>
    /// <para>
    /// <b>「证明停稳」</b>：RIoT 车辆读数在线、空闲、速度为零、无在执行任务、新鲜，并且 RIoT 的运动安全读数为停止。任一不满足、或读不到，
    /// 都不收尾——「取消即释放而车其实还在动」是这一段要堵的事。等待点预占不在这里放：车若停在点上，它由离点清扫在车离开之后放。
    /// </para>
    /// </remarks>
    private async Task JudgeEndedIdleReturnOrderAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        string endCode,
        string holdCode,
        DateTimeOffset now,
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
            await HoldIdleReturnAsync(runtime, holdCode, now, cancellationToken).ConfigureAwait(false);
            return;
        }

        DateTimeOffset readAt = timeProvider.GetUtcNow();
        bool stoppedWithoutOrder = vehicle.Connected && vehicle.ProcState == "IDLE" && vehicle.Speed == 0 &&
                                   string.IsNullOrWhiteSpace(vehicle.OrderTaskId) &&
                                   vehicle.ObservedAt <= readAt && readAt - vehicle.ObservedAt <= runtimeOptions.MaximumEvidenceAge &&
                                   safety.MotionState == RiotVehicleMotionState.Stopped;
        if (!stoppedWithoutOrder)
        {
            await HoldIdleReturnAsync(runtime, holdCode, now, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Standing on the point it was heading for: the plan keeps its leg, and the reservation stays for the departure sweep.
        await EndIdleReturnAsync(
                runtime, stop, endCode, stillAtWaitingPoint: vehicle.CurrentStationId == stop.StationRiotId &&
                                                            endCode != IdleReturnExecutionReasons.WaitingPointLost,
                releaseStationNow: null, now, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HoldIdleReturnAsync(
        JourneyRuntimeRow runtime,
        string code,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.Equals(runtime.BlockReasonCode, code, StringComparison.Ordinal))
        {
            return;
        }
        LogIdleReturnHeld(logger, code, runtime.JourneyId, runtime.VehicleKey, null);
        runtime.SetBlockReason(code, now);
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EndIdleReturnAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        string code,
        bool stillAtWaitingPoint,
        StationExclusivityRow? releaseStationNow,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        string? detail = null)
    {
        checkpointWaits.Clear(runtime.VehicleKey);
        await IdleReturnEnding.StageAsync(
                dbContext, runtime, stop, code, code, stillAtWaitingPoint, releaseStationNow, now, cancellationToken)
            .ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogIdleReturnEnded(
            logger, runtime.JourneyId, runtime.VehicleKey, detail is null ? code : $"{code} ({detail})", stop.StationRiotId, null);
        await JourneyClosure.SendAsync(publisher, dbContext, runtime.AgvId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>这张单本服务端发过取消（命令审计里有），不再发第二次。</summary>
    private Task<bool> OwnCancelIssuedAsync(string orderId, CancellationToken cancellationToken)
    {
        string cancel = RiotCommandTypeNames.For(RiotOrderCommandKind.Cancel);
        return dbContext.RiotOrderCommandAudit.AsNoTracking()
            .AnyAsync(row => row.CommandType == cancel && row.TargetOrderId == orderId, cancellationToken);
    }

    /// <summary>
    /// 建单前重新核验等待点（<c>REQ-0293</c> 前半句、<c>REQ-0296</c> 第一支）：登记、停用、白名单、专用角色与实时目录，与承诺时同一个判定函数。
    /// 合格答空，否则答那一条的码。
    /// </summary>
    private async Task<string?> IdleReturnPointIneligibleAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        RiotMapStationCatalogSnapshot currentMap,
        CancellationToken cancellationToken)
    {
        WaitingPointRegistrationVersion? registration =
            await WaitingPointRegistry.ReadCurrentFromAsync(dbContext, cancellationToken).ConfigureAwait(false);
        IReadOnlySet<int> fixedTaskStations = WaitingPointFixedTaskStations.StationIds(
            await WaitingPointFixedTaskStations.ReadAsync(_taskTypeStations.Bindings, runtime.MapId, cancellationToken)
                .ConfigureAwait(false));
        WaitingPointEligibilityDecision decision = WaitingPointEligibility.Judge(
            registration, fixedTaskStations, currentMap, runtime.MapId, stop.StationRiotId, runtime.VehicleKey);
        return decision.Accepts ? null : decision.Reason;
    }

    /// <summary>
    /// 非业务移动的出发前安全门（调度 2026-09-29，program#150 审查）：v2 的 <c>PreDepartureSafetyCheck</c> 必须带需求与移动段，空闲返回发不出，
    /// 也不许编一个 id 去填。服务端在建 RIoT 单之前自己核同一组事实，空表即放行。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 判据，全部要满足：
    /// </para>
    /// <list type="number">
    /// <item>会话的就绪判定是可移动：会话行此刻是 <c>Ready</c>——那是 <c>WireToGateStore.DecideReadinessAsync</c> 最近一次落库的结论，
    /// 每一次车载端安全状态变化它都在入站事务里重判一次。这里读它、不重跑它：重跑会在入站事务之外写会话行。</item>
    /// <item>车况（<see cref="VehicleConditionReasonsAsync"/>，与自建单重建、从没发出的腿补建同一组）：车载端安全摘要说可以出发
    /// （离站安全、停稳、仓位锁闭、开锁输出复位、无未知），没有故障，在本图，RIoT 报停止。</item>
    /// <item>全部仓位锁闭、无阻断事实：这一代会话最新一张安全状态快照里八个仓位都是 <c>LOCKED</c>、<c>RESET</c>，当前那一版摘要的
    /// <c>reasonCodes</c> 为空（<see cref="Dispatch.OnboardDispatchFactsReader.ReadDepartureSafetyGapsAsync"/>）。</item>
    /// </list>
    /// <para>
    /// <b>「足够新」</b>：安全状态是会话内的事实，不按报文时刻老化；老化的是会话存活——这一代会话最后一次说话距今不超过
    /// <c>JourneyRuntime:MaximumEvidenceAge</c>（与派车读车载端事实同一个时限，默认 2 分钟），摘要取会话当前那一版
    /// <c>safetyStateVersion</c>。死掉的对端留下的 Ready 行与旧快照因此不算数。
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<string>> IdleReturnDepartureGapsAsync(
        JourneyRuntimeRow runtime,
        CancellationToken cancellationToken)
    {
        List<string> gaps = [.. await VehicleConditionReasonsAsync(runtime, cancellationToken).ConfigureAwait(false)];
        SessionRecoveryRow? session = await CurrentReadySessionAsync(runtime.AgvId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            gaps.Add("ONBOARD_SESSION_NOT_READY");
        }
        else if (session.SafetyRevision is not long safetyRevision)
        {
            gaps.Add("SAFETY_STATE_UNREADABLE");
        }
        else
        {
            gaps.AddRange(await onboardFacts
                .ReadDepartureSafetyGapsAsync(runtime.AgvId, session.SessionGeneration, safetyRevision, cancellationToken)
                .ConfigureAwait(false));
        }
        return [.. gaps.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// 单确认之后，把途中的计划（一条 <c>ACTIVE</c> 的等待点腿）与业务状态（<c>IDLE_RETURN</c>）暂存、保存、发出。
    /// </summary>
    /// <remarks>
    /// 消息 id 与修订号取自旅程行（物化时按 <c>StableUuid(journeyId|用途)</c> 派生、按车计数器定基准），所以补发与重跑都是同一张：
    /// 暂存原语对已在发件箱里的 id 什么也不加。发不出去（没连着、握手中）不算失败，留在发件箱里等 <see cref="ReplayIdleReturnSnapshotsAsync"/>。
    /// </remarks>
    private async Task PublishIdleReturnSnapshotsAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        CancellationToken cancellationToken)
    {
        SessionRecoveryRow? session = await dbContext.SessionRecoveries.AsNoTracking()
            .SingleOrDefaultAsync(row => row.AgvId == runtime.AgvId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        bool stagedPlan = await OnboardJourneyPublisher.StageUpcomingStopPlanAsync(
            store,
            runtime.PlanMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            JourneyPlanBuilder.IdleReturnPlan(runtime, stop, arrived: false, runtime.PlanRevision),
            now,
            cancellationToken).ConfigureAwait(false);
        bool stagedState = await OnboardJourneyPublisher.StageVehicleBusinessStateAsync(
            store,
            runtime.VehicleBusinessMessageId,
            runtime.AgvId,
            session.SessionGeneration,
            JourneyPlanBuilder.IdleReturnBusinessState(runtime.VehicleBusinessRevision),
            // A millisecond after the plan, so a replay -- which sends in creation order -- keeps the vector's order too.
            now.AddMilliseconds(1),
            cancellationToken).ConfigureAwait(false);
        if (stagedPlan || stagedState || dbContext.ChangeTracker.HasChanges())
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        // The plan first, then the business state: CV-WAITING-POINT-IDLE-RETURN's orderedExpectedMessages.
        await SendIdleReturnSnapshotAsync(runtime.PlanMessageId, cancellationToken).ConfigureAwait(false);
        await SendIdleReturnSnapshotAsync(runtime.VehicleBusinessMessageId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>车重连之后，把这一趟没被确认的途中快照按原 id 补发，不产生第二份。</summary>
    private async Task ReplayIdleReturnSnapshotsAsync(
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
                    new HashSet<string>(StringComparer.Ordinal) { runtime.VehicleBusinessMessageId, runtime.PlanMessageId },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // Not on the line this moment; the next round, or the next reconnect, sends them.
        }
    }

    private async Task SendIdleReturnSnapshotAsync(string messageId, CancellationToken cancellationToken)
    {
        try
        {
            await publisher.SendPersistedAsync(messageId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // Not on the line this moment: kept in the outbox for the replay.
        }
    }
}
