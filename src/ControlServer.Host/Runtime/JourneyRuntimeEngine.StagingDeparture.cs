using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Release;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 待送点取货停靠上的两种「走」（control-server#713，CP-0012、CP-0013，协议 4.0.0 候选 wire-notes「Staging pickup stops」三节）：
/// 客户在车载屏上点了出发，与车缺电而车上有货。两种都把没装的需求放回待派、带已装的货走正常离站；一篮没装时出发让旅程原地收尾。
/// </summary>
/// <remarks>
/// <para>
/// <b>只在开关打开时有事可做。</b>开关关着时没有待送点取货停靠（<see cref="IsStagingPickup"/> 恒假），缺电带货一支一次车况都不读；出发请求
/// 照样要答，答的是 <c>ACTION_NOT_ALLOWED_IN_STATE</c>（v3 的车载端根本发不出这条消息，开关关着时线上逐字节不变由 WirePin 钉住）。
/// </para>
/// <para>
/// <b>放回的做法与持货截断（control-server#290）相同</b>，只差三处：范围只是待送点取货停靠（当前及之后，<c>Sequence</c> 不小于当前），不碰机台取货；
/// 空清单的结束原因说 <c>OPERATOR_DEPARTED</c> / <c>CHARGING_REQUIRED</c>（<see cref="StopEndedReasons"/>）；装货阶段在同一次改动里关闭、车辆业务
/// 状态快照同一次暂存，崩在中间不会留下「需求放回了、车上还显示在装」。放回记 <see cref="DemandJourneyLookup.ReleasedForRedispatchReason"/>：
/// 需求仍是 <c>Accepted</c>、不写抑制、等待年龄不重置、不计 control-server#703 的站点超时次数（它只数
/// <see cref="DemandJourneyLookup.ReleasedAtStationDeadlineReason"/>）。
/// </para>
/// <para>
/// <b>离站照旧</b>：装货阶段以出发原因关闭之后，待送点的离站等待立即结束（<see cref="StationDepartureWaitIsOverAsync"/>），纠错闸、仓门扣车、
/// <c>DEPARTURE</c> 离站核验一样不少。缺电带货不插充电停靠：旅程按原有卸货停靠送完、收尾之后，由现有充电分配接手（CP-0013）。
/// </para>
/// </remarks>
public sealed partial class JourneyRuntimeEngine
{
    private static readonly Action<ILogger, string, string, string, string, int, int, Exception?> LogDepartureAnswered =
        LoggerMessage.Define<string, string, string, string, int, int>(
            LogLevel.Information,
            new EventId(2314, nameof(LogDepartureAnswered)),
            "Departure request {DepartureRequestId} from operator {OperatorId} on vehicle {AgvId} answered {Outcome}; " +
            "{LoadedBaskets} basket(s) on board, {ReleasedDemands} demand(s) released back to dispatch.");

    private static readonly Action<ILogger, string, string, string, Exception?> LogDepartureReleasedDemand =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(2315, nameof(LogDepartureReleasedDemand)),
            "Demand {DemandId} went back to the backlog because vehicle {AgvId} left the staging station ({Reason}).");

    private static readonly Action<ILogger, string, int, int, int, Exception?> LogStagingLowBatteryDepartsWithCargo =
        LoggerMessage.Define<string, int, int, int>(
            LogLevel.Warning,
            new EventId(2316, nameof(LogStagingLowBatteryDepartsWithCargo)),
            "Vehicle {AgvId} waits at the staging station with {LoadedBaskets} basket(s) on board and its battery at " +
            "{BatteryPercent}% below the mandatory charge line of {EntryThreshold}%; it delivers what it carries first and " +
            "charges once the journey ends.");

    private static readonly Action<ILogger, string, Exception?> LogStagingLowBatteryAnchorUnloaded =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2317, nameof(LogStagingLowBatteryAnchorUnloaded)),
            "Vehicle {AgvId} is below the mandatory charge line at the staging station with cargo on board, but the journey's " +
            "first demand is not loaded; it does not depart on its own.");

    /// <summary>
    /// 出发或缺电的放回落下了什么：旅程原地收尾，或带着已装的货进离站；机台取货停靠上装货阶段已结束时受理的出发（control-server#725）只结束站点离站等待，
    /// 没有要补发的业务快照。
    /// </summary>
    private enum StagingDepartureEffect
    {
        JourneyClosed,
        Departing,
        StationWaitEnded
    }

    /// <summary>
    /// 答这辆车还没答复的出发请求（见 <see cref="CurrentStopDepartures"/>）。接受了一条就返回真：旅程已变（收尾或进离站），调用方重新加载，
    /// 其余请求下一轮再答（它们多半已经过时）。
    /// </summary>
    private async Task<bool> AnswerDepartureRequestsAsync(
        JourneyRuntimeRow runtime,
        SessionRecoveryRow session,
        bool holdingApplicable,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CurrentStopDepartures.Request> pending = await CurrentStopDepartures
            .UnansweredAsync(dbContext, runtime.AgvId, cancellationToken).ConfigureAwait(false);
        foreach (CurrentStopDepartures.Request request in pending)
        {
            if (await AnswerDepartureRequestAsync(runtime, request, session, holdingApplicable, now, cancellationToken)
                    .ConfigureAwait(false))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 判一条出发请求，答复、放回与装货阶段的关闭同一次提交（向量 <c>departure-decision-and-releases-durable-before-result</c>），提交之后
    /// 先发答复、再发业务快照（<c>CV-CURRENT-STOP-DEPARTURE-ACCEPTED</c> 的顺序）。接受了返回真。
    /// </summary>
    private async Task<bool> AnswerDepartureRequestAsync(
        JourneyRuntimeRow runtime,
        CurrentStopDepartures.Request request,
        SessionRecoveryRow session,
        bool holdingApplicable,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string resultId = CurrentStopDepartures.ResultMessageId(request.DepartureRequestId);
        StagingDepartureEffect? effect = null;
        WireProblem? problem;
        int loadedBaskets;
        int released = 0;
        await using (IDbContextTransaction? transaction = dbContext.Database.CurrentTransaction is null
                         ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
                         : null)
        {
            // 写锁里重读：阶段、停靠与归属都可能在这一轮开头之后被别的连接改过（扫码前取消、纠错的授权都在入站写）。
            JourneyRuntimeStage? stageNow = await dbContext.JourneyRuntimes.AsNoTracking()
                .Where(row => row.JourneyId == runtime.JourneyId)
                .Select(row => (JourneyRuntimeStage?)row.Stage)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (stageNow is null or JourneyRuntimeStage.Completed ||
                await dbContext.ProtocolOutbox.AsNoTracking()
                    .AnyAsync(row => row.MessageId == resultId, cancellationToken).ConfigureAwait(false))
            {
                // 旅程刚收尾：留给没有旅程的那一支扫尾答。已有答复：别处刚答过。
                return false;
            }
            JourneyStopCursor locked = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
            CurrentStopDepartureRule.Facts facts = await DepartureFactsAsync(
                runtime, locked, session, stageNow.Value, request.WorklistRevision, cancellationToken).ConfigureAwait(false);
            problem = CurrentStopDepartureRule.Decide(request, facts);
            loadedBaskets = LoadedBaskets(locked);
            await OnboardJourneyPublisher.StageCurrentStopDepartureResultAsync(
                new WireToGateStore(dbContext),
                resultId,
                runtime.AgvId,
                session.SessionGeneration,
                request.DepartureRequestId,
                problem,
                facts.CurrentWorklistRevision,
                now,
                cancellationToken).ConfigureAwait(false);
            if (problem is null)
            {
                (effect, released) = IsStagingPickup(locked.Current)
                    ? await StageStagingDepartureAsync(
                        runtime,
                        locked,
                        session,
                        holdingApplicable,
                        CurrentStopDepartures.OperatorDepartedReason,
                        LoadingClosedReasons.OperatorDeparted,
                        now.AddMilliseconds(1),
                        cancellationToken).ConfigureAwait(false)
                    : await StageMachineDepartureAsync(
                        runtime, locked, session, holdingApplicable, now.AddMilliseconds(1), cancellationToken).ConfigureAwait(false);
            }
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await SendStoredAsync(resultId, cancellationToken).ConfigureAwait(false);
        await SendStagingDepartureAsync(runtime, effect, cancellationToken).ConfigureAwait(false);
        LogDepartureAnswered(
            logger, request.DepartureRequestId, request.OperatorId, runtime.AgvId,
            problem is null ? "ACCEPTED" : $"REJECTED ({problem.ReasonCode})", loadedBaskets, released, null);
        return effect is not null;
    }

    /// <summary>
    /// 待送点取货停靠上车缺电而车上有货：按自动出发处理（CP-0013，<c>CV-STAGING-CHARGING-REQUIRED-DEPARTURE-WITH-CARGO</c>）——装货阶段以
    /// <c>CHARGING_REQUIRED</c> 关闭，没装的放回待派，带着已装的货离站去送，不插充电停靠，旅程收尾之后由充电分配接手。做了返回真。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 一篮没装不归这里：那一支是 control-server#709 的原地收尾（<see cref="TryReleaseLowBatteryStagingStopAsync"/>），收尾原因同样说
    /// <c>CHARGING_REQUIRED</c>。已经判为装满该走（<c>VEHICLE_FULL</c>）或已经在出发的不再改判：车本来就要走了。
    /// </para>
    /// <para>
    /// 电量的读法与那一支相同（<see cref="MandatoryChargeBatteryAsync"/>），读不到、读数过期都不动。写锁里重读阶段与开着的扫码前取消；
    /// 有仓位操作在跑（<c>AwaitingLoadResult</c>）不动，等它闭环。
    /// </para>
    /// </remarks>
    private async Task<bool> TryDepartLowBatteryStagingStopWithCargoAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor stops,
        SessionRecoveryRow session,
        bool holdingApplicable,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!StandsAtStaging(stops) ||
            runtime.Stage is not (JourneyRuntimeStage.AwaitingSublot or JourneyRuntimeStage.AwaitingStationDeparture) ||
            // 已经判为装满该走、或已经以出发／缺电关闭：车本来就要走了。分区不许途中追加时，装完本站的装货阶段是 CLOSED / PLANNED_LOADING_COMPLETE，
            // 而车在待送点上照样等客户（control-server#709），这一种要管。
            runtime.LoadingPhaseState == LoadingPhaseStates.VehicleFull ||
            // 持货期限已到（control-server#727）同样是「车本来就要走了」，不改判成缺电。
            runtime.LoadingPhaseState == LoadingPhaseStates.Closed && StagingDepartureUnderWay(runtime) ||
            !stops.AllDemands.Any(item => item.Membership.Status == JourneyDemandStatuses.Loaded) ||
            stops.AllDemands.Any(item => item.Membership.Status == JourneyDemandStatuses.Loading))
        {
            return false;
        }
        if (await MandatoryChargeBatteryAsync(runtime, cancellationToken).ConfigureAwait(false) is not { } battery)
        {
            return false;
        }

        StagingDepartureEffect effect;
        await using (IDbContextTransaction? transaction = dbContext.Database.CurrentTransaction is null
                         ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
                         : null)
        {
            JourneyRuntimeStage? stageNow = await dbContext.JourneyRuntimes.AsNoTracking()
                .Where(row => row.JourneyId == runtime.JourneyId)
                .Select(row => (JourneyRuntimeStage?)row.Stage)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            JourneyStopCursor locked = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
            if (stageNow is not (JourneyRuntimeStage.AwaitingSublot or JourneyRuntimeStage.AwaitingStationDeparture) ||
                locked.OpenStops.Count == 0 ||
                !IsStagingPickup(locked.Current) ||
                locked.AllDemands.Any(item => item.Membership.Status == JourneyDemandStatuses.Loading) ||
                !locked.AllDemands.Any(item => item.Membership.Status == JourneyDemandStatuses.Loaded) ||
                await OpenCancellationAtCurrentStopAsync(locked, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
            if (AnchorUnloaded(runtime, locked))
            {
                // 每趟旅程只记一次（独立审查 L2）：这一支每轮都会走到，状态不变就不该每轮刷一条 Warning。
                if (_stagingDepartureWarnings.AnchorUnloadedWarned.TryAdd(runtime.JourneyId, 0))
                {
                    LogStagingLowBatteryAnchorUnloaded(logger, runtime.AgvId, null);
                }
                return false;
            }
            (effect, _) = await StageStagingDepartureAsync(
                runtime,
                locked,
                session,
                holdingApplicable,
                CurrentStopDepartures.ChargingRequiredReason,
                LoadingClosedReasons.ChargingRequired,
                now,
                cancellationToken).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            LogStagingLowBatteryDepartsWithCargo(
                logger, runtime.AgvId, LoadedBaskets(locked), battery.Percent, battery.EntryThreshold, null);
        }
        await SendStagingDepartureAsync(runtime, effect, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 出发与缺电共用的那一次改动（只暂存，调用方保存）：一篮没装就全部放回、旅程原地收尾；否则放回待送点上没开始装的、关闭装货阶段、
    /// 暂存那张业务状态快照与（本站因此空了时）那张空清单。返回落下的是哪一种，和放回了几条。
    /// </summary>
    private async Task<(StagingDepartureEffect Effect, int Released)> StageStagingDepartureAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor locked,
        SessionRecoveryRow session,
        bool holdingApplicable,
        string stopReason,
        string closedReason,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        JourneyStopRow current = locked.Current;
        string? sublotRequestMessageId = locked.CurrentSublotRequestMessageIdOrNone(runtime.WorklistRevision);
        bool loaded = locked.AllDemands.Any(item =>
            item.Membership.Status is JourneyDemandStatuses.Loaded or JourneyDemandStatuses.Loading);

        JourneyStopDemand[] leaving = loaded
            ? [.. await UnstartedAtStagingAsync(runtime, locked, cancellationToken).ConfigureAwait(false)]
            // 一篮没装：旅程上还没终结的需求全部放回（锚也在内，旅程随即收尾，不受锚的限制），与 control-server#709 缺电那一支同一个做法。
            : [.. locked.Demands.Where(item =>
                item.Demand.Status == DemandExecutionStatus.Accepted &&
                item.Membership.Status == JourneyDemandStatuses.PendingLoad)];
        int releasedFromCurrentStop = leaving.Count(item =>
            string.Equals(item.Membership.PickupStopId, current.StopId, StringComparison.Ordinal));
        string[] leavingIds = [.. leaving.Select(item => item.Demand.DemandId)];
        foreach (string demandId in leavingIds)
        {
            JourneyDemandRow membership = await dbContext.Set<JourneyDemandRow>()
                .SingleAsync(
                    row => row.JourneyId == runtime.JourneyId && row.DemandId == demandId && row.RemovedAt == null,
                    cancellationToken)
                .ConfigureAwait(false);
            await DemandReleaseService.StageReleasedMembershipAsync(dbContext, membership, at, cancellationToken)
                .ConfigureAwait(false);
            LogDepartureReleasedDemand(logger, demandId, runtime.AgvId, stopReason, null);
        }
        if (releasedFromCurrentStop > 0)
        {
            // 版数守恒，与持货截断、站点期限放回同一个理由：这个停靠的归属少了几条就补几版，退役本站较早各版清单才数得全。
            (await TrackedStopAsync(current.StopId, cancellationToken).ConfigureAwait(false)).WorklistRefills +=
                releasedFromCurrentStop;
        }

        if (!loaded)
        {
            // 车停在这一站、不修订计划，三套占用一起放；车上收尾快照的 stopEndedReason 是 OPERATOR_DEPARTED / CHARGING_REQUIRED，loadingPhase 为空。
            await new PickupStopTermination(dbContext).StageJourneyClosureAsync(
                runtime, sublotRequestMessageId, stopReason, at, cancellationToken).ConfigureAwait(false);
            return (StagingDepartureEffect.JourneyClosed, leavingIds.Length);
        }

        if (leavingIds.Length > 0)
        {
            await JourneyPlanRevisionStage.StageAsync(
                    dbContext, runtime.JourneyId, leavingIds, currentStopMayGo: false, routing: null, cancellationToken)
                .ConfigureAwait(false);
        }

        // 已经判为装满该走（VEHICLE_FULL，比如被门禁扣在离站等待里）：照样接受、照样放回没开始装的，但不改判关闭原因、不重发业务快照——
        // 与缺电那一支「不改判」同一个口径（独立审查 L3）。车本来就要走，装满是更早、更具体的那个理由。
        if (runtime.LoadingPhaseState != LoadingPhaseStates.VehicleFull)
        {
            await StageLoadingPhaseClosedAsync(runtime, locked, session, holdingApplicable, closedReason, at, cancellationToken)
                .ConfigureAwait(false);
        }

        // 本站因此没有待做项了：空清单说出发或缺电（wire-notes 第 4 条）。本站本来就装完了（持货等单）时不再补一张：车上手里那张清单照旧，
        // 离站由业务状态快照与离站核验表达（CP-0012 第三节第 5 条由本票定，见 PR）。
        await StopEndWorklist.StageAfterReleaseAsync(
            dbContext, runtime, releasedFromCurrentStop > 0, sublotRequestMessageId, stopReason, at.AddMilliseconds(1),
            cancellationToken).ConfigureAwait(false);
        return (StagingDepartureEffect.Departing, leavingIds.Length);
    }

    /// <summary>
    /// 装货阶段以 <paramref name="closedReason"/> 关闭，业务状态快照与它同一次改动（<c>PublishLoadingPhaseAsync</c> 的号与退役，只是暂存而不发）。
    /// 车停在这个停靠上，按「已到站」算号。待送点的出发与缺电、机台取货装货阶段还开着时的出发（control-server#725）共用。
    /// </summary>
    private async Task StageLoadingPhaseClosedAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor locked,
        SessionRecoveryRow session,
        bool holdingApplicable,
        string closedReason,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        JourneyStopRow current = locked.Current;
        runtime.LoadingPhaseState = LoadingPhaseStates.Closed;
        runtime.LoadingClosedReason = closedReason;
        runtime.UpdatedAt = at;
        runtime.VehicleBusinessRevision += 1;
        long revision = StopRevision(runtime.VehicleBusinessRevision, current);
        await FenceSupersededSnapshotAsync(current.VehicleBusinessMessageId, cancellationToken).ConfigureAwait(false);
        await FenceSupersededSnapshotAsync(LoadingPhaseMessageId(current, revision - 1), cancellationToken).ConfigureAwait(false);
        await OnboardJourneyPublisher.StageVehicleBusinessStateAsync(
            new WireToGateStore(dbContext),
            LoadingPhaseMessageId(current, revision),
            runtime.AgvId,
            session.SessionGeneration,
            TransportBusinessState(revision, CurrentLoadingPhase(runtime, holdingApplicable, locked), PublishedBatteryState(runtime)),
            at,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 机台取货停靠上受理了一次出发（control-server#725，CP-0014 <c>REQ-0354</c>；wire-notes 出发一节第 6 条）：记下本站已由操作员结束
    /// （<see cref="JourneyStopRow.OperatorDepartedAt"/>，离站等待据此立即结束），装货阶段还开着时再按持货期限此刻到期处理。只暂存，调用方保存。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>装货阶段还开着</b>（适用持货等单，<c>LOADING</c> 或 <c>CARGO_HOLDING_WAIT</c>）：等同持货期限此刻到期——装货阶段以 <c>OPERATOR_DEPARTED</c>
    /// 关闭（不是 <c>CARGO_HOLDING_TIMEOUT</c>，车载端要分得清人按的和到期的），没开始装的走持货到期的同一条截断
    /// （<see cref="StageCargoHoldingTruncationAsync"/>），只把车上空清单的结束原因换成 <c>OPERATOR_DEPARTED</c>。不另写一套释放逻辑：两条路以后不会分叉。
    /// 本站此刻没有待做项（受理的前提），所以截掉的都在后面的停靠上，本站不补空清单；计划因此变了，由调用方在答复与业务快照之后重发。
    /// </para>
    /// <para>
    /// <b>装货阶段已经结束</b>（<c>CLOSED</c>，或 <c>VEHICLE_FULL</c>，或不适用持货等单）：只剩站点离站等待，装货阶段与 <c>closedReason</c> 一样不动、
    /// 不发业务快照（向量 <c>CV-CURRENT-STOP-DEPARTURE-MACHINE-STATION-WAIT</c> 的 <c>closed-reason-rewritten-by-departure</c>）。
    /// <c>VEHICLE_FULL</c> 算在这里，与待送点那一支「装满是更早、更具体的理由，不改判」同一个口径。不适用持货的旅程落库的是 <c>CLOSED</c>／
    /// <c>PLANNED_LOADING_COMPLETE</c>，车上看到的还是最后那张 <c>LOADING</c>（wire-notes 出发一节第 1 条），同样不碰。
    /// </para>
    /// </remarks>
    private async Task<(StagingDepartureEffect Effect, int Released)> StageMachineDepartureAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor locked,
        SessionRecoveryRow session,
        bool holdingApplicable,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        (await TrackedStopAsync(locked.Current.StopId, cancellationToken).ConfigureAwait(false)).OperatorDepartedAt = at;
        if (!holdingApplicable ||
            runtime.LoadingPhaseState is not (null or LoadingPhaseStates.Loading or LoadingPhaseStates.CargoHoldingWait))
        {
            return (StagingDepartureEffect.StationWaitEnded, 0);
        }

        await StageLoadingPhaseClosedAsync(
            runtime, locked, session, holdingApplicable, LoadingClosedReasons.OperatorDeparted, at, cancellationToken).ConfigureAwait(false);
        string[] released = await StageCargoHoldingTruncationAsync(
            runtime, CurrentStopDepartures.OperatorDepartedReason, at, cancellationToken).ConfigureAwait(false) ?? [];
        foreach (string demandId in released)
        {
            LogDepartureReleasedDemand(logger, demandId, runtime.AgvId, CurrentStopDepartures.OperatorDepartedReason, null);
        }
        return (runtime.Stage == JourneyRuntimeStage.Completed ? StagingDepartureEffect.JourneyClosed : StagingDepartureEffect.Departing,
            released.Length);
    }

    /// <summary>
    /// 带着货出发时要放回的：待送点取货停靠（当前那一个与之后的）上归属待装、需求仍受理中的。锚需求不在内（见 <see cref="CurrentStopDepartureRule"/>）；
    /// 之后的停靠已有订单意图的不在内，与持货截断同一个判据（离站已授权、开往它的单已存，截掉会让同一辆车两张单）。
    /// </summary>
    private async Task<List<JourneyStopDemand>> UnstartedAtStagingAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor locked,
        CancellationToken cancellationToken)
    {
        JourneyStopRow current = locked.Current;
        List<JourneyStopDemand> unstarted = [];
        foreach (JourneyStopDemand item in locked.AllDemands)
        {
            if (item.Demand.Status != DemandExecutionStatus.Accepted ||
                item.Membership.Status != JourneyDemandStatuses.PendingLoad ||
                string.Equals(item.Demand.DemandId, runtime.DemandId, StringComparison.Ordinal))
            {
                continue;
            }
            JourneyStopRow pickup = locked.Stops.Single(row => row.StopId == item.Membership.PickupStopId);
            if (pickup.Sequence < current.Sequence || !IsStagingPickup(pickup))
            {
                continue;
            }
            if (!string.Equals(pickup.StopId, current.StopId, StringComparison.Ordinal) &&
                await dbContext.OrderIntents.AsNoTracking()
                    .AnyAsync(row => row.UpperId == pickup.UpperId, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }
            unstarted.Add(item);
        }
        return unstarted;
    }

    /// <summary>判一条出发请求要的事实，在写锁里读（<see cref="CurrentStopDepartureRule.Facts"/>）。</summary>
    private async Task<CurrentStopDepartureRule.Facts> DepartureFactsAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor locked,
        SessionRecoveryRow session,
        JourneyRuntimeStage stageNow,
        long requestedRevision,
        CancellationToken cancellationToken)
    {
        long revision = await CurrentStopDepartures.CurrentWorklistRevisionAsync(dbContext, runtime.AgvId, cancellationToken)
            .ConfigureAwait(false);
        if (locked.OpenStops.Count == 0)
        {
            return new CurrentStopDepartureRule.Facts(null, null, revision, false, false, false, false, false, false, false);
        }
        JourneyStopRow current = locked.Current;
        bool loaded = locked.AllDemands.Any(item =>
            item.Membership.Status is JourneyDemandStatuses.Loaded or JourneyDemandStatuses.Loading);
        bool atMachinePickup = current.StopRole == JourneyStopRoles.Pickup && !IsStagingPickup(current);
        return new CurrentStopDepartureRule.Facts(
            current.StationId,
            current.OperationSessionId,
            revision,
            AtStagingPickup: IsStagingPickup(current),
            AtStopAwaitingDeparture: stageNow is JourneyRuntimeStage.AwaitingSublot or JourneyRuntimeStage.AwaitingStationDeparture,
            SlotOperationRunning: locked.AllDemands.Any(item => item.Membership.Status == JourneyDemandStatuses.Loading),
            CorrectionOrCancellationOpen:
                await OpenCancellationAtCurrentStopAsync(locked, cancellationToken).ConfigureAwait(false) ||
                await OpenCorrectionOnBoardAsync(runtime, locked, cancellationToken).ConfigureAwait(false),
            // 待送点持货到期（control-server#727）也算已在出发：再按一次出发不把 CARGO_HOLDING_TIMEOUT 改判成 OPERATOR_DEPARTED。只在待送点这么认：
            // 机台取货持货到期之后还在等站点离站期限，CP-0014 要那时的出发照样受理、原因不变。机台上「已在出发」是本停靠已受理过一次（control-server#725），
            // 按停靠认而不按关闭原因认：同一趟旅程后面的机台取货停靠仍可以出发。
            AlreadyDeparting: IsStagingPickup(current)
                ? runtime.LoadingPhaseState == LoadingPhaseStates.Closed && StagingDepartureUnderWay(runtime)
                : current.OperatorDepartedAt is not null,
            AnchorUnloadedWithCargo: loaded && AnchorUnloaded(runtime, locked),
            NothingLoadedAndDoorsNotProvenClosed: !loaded && SlotDoorsNotProvenClosed(session),
            // 本站装完（CP-0014）：本停靠已承诺的都已装上或已结束、本站至少装上一篮、车停在离站等待里（还没发离站核验）。只看本站的归属——
            // 车上别处装的货不算「这一站装完」，本站需求全被取消、一篮没装的停靠按现有规则收尾，不出发（CP-0014 起草时定第 7 项）。
            MachinePickupLoadsComplete: atMachinePickup &&
                                        stageNow == JourneyRuntimeStage.AwaitingStationDeparture &&
                                        locked.OutstandingAtCurrentStop.Count == 0 &&
                                        locked.AllAtStop(current).Any(item => item.Membership.Status == JourneyDemandStatuses.Loaded),
            StaleWorklistWithoutSubstantiveChange: requestedRevision < revision &&
                                                   await StaleWorklistWithoutSubstantiveChangeAsync(
                                                       runtime, locked, requestedRevision, revision, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 出发请求带的清单号 <paramref name="requested"/> 已被顶掉，但它与此刻的 <paramref name="current"/> 之间没有实质差别（control-server#742 扩项，用户 10-17 定，
    /// CP-0015；口径调度 10-17 核定）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 现场 agv03 140 站：本站装上一篮之后取消了另外两条，取消那一轮发了空清单（30），引擎随即补发只多一个期限的空清单（31）；按着 30 点的出发晚 62 ms 到，被拒，
    /// 车多停了 3 分多钟。两版对操作员要做的事是同一个答案：本站没有要录的，已装的没变。
    /// </para>
    /// <para>
    /// 四条都成立才算没有差别：
    /// </para>
    /// <list type="number">
    /// <item>请求号落在当前停靠自己的清单号区间里（不小于 <see cref="JourneyStopCursor.FirstWorklistRevisionAt"/>），而且从请求号到当前号每一版都是按本停靠的
    /// 消息 id 落进发件箱的、站是当前站：请求指的是车此刻停着的这一站，不是上一站留下的清单。</item>
    /// <item>请求带的作业会话不在这里比：<see cref="CurrentStopDepartureRule"/> 照旧拿它与当前停靠比，为空不比。</item>
    /// <item>从请求号那一版到当前号，每一版的 <c>items</c> 都为空：车屏上那一版已经没有待做项，之后也再没有过。</item>
    /// <item>请求号那一版落库之后，本站的已装集合没变，多了少了都不行，之后也没开过纠错（<see cref="LoadsAtStopUnchangedSinceAsync"/>，只用服务端的钟）。</item>
    /// </list>
    /// <para>
    /// 只对机台取货停靠；待送点上过期请求照旧答过期（CP-0015 修订项一，待送点另议）。
    /// </para>
    /// <para>
    /// 「多了」一侧在构造上由第 3 条推出——装上一篮之前，它必先作为待做项出现在某一版清单里；途中追加不并入当前停靠（REQ-0196，
    /// <c>EnRouteAppendPlanner.MergeTargetAt</c>），本停靠不会凭空多出待做项。这里照样直接比，不只靠推论：那两个前提哪天被改了，第 3 条单独放行的就会是
    /// 一个已装集合变过的请求。判据是 <c>StaleWorklistDepartureTests.AStaleRequestIsRefusedWhenTheLoadsAtTheStopChangedAfterItsWorklist</c>；
    /// 「少了」一侧推不出来（已装的被终结不会让任何一版多出待做项），判据是 <c>AStaleRequestIsRefusedWhenALoadedBasketLeftTheStopAfterItsWorklist</c>。
    /// </para>
    /// </remarks>
    private async Task<bool> StaleWorklistWithoutSubstantiveChangeAsync(
        JourneyRuntimeRow runtime,
        JourneyStopCursor locked,
        long requested,
        long current,
        CancellationToken cancellationToken)
    {
        JourneyStopRow stop = locked.Current;
        // 只对机台取货（审查建议 2，CP-0015 修订项一只对机台取货停靠；待送点另议）：待送点上过期请求照旧答过期。
        if (stop.StopRole != JourneyStopRoles.Pickup || IsStagingPickup(stop) ||
            requested < locked.FirstWorklistRevisionAt(runtime.WorklistRevision, stop))
        {
            return false;
        }
        string[] ids = [.. Enumerable.Range(0, checked((int)(current - requested + 1)))
            .Select(offset => locked.WorklistMessageIdAt(runtime.WorklistRevision, stop, requested + offset))];
        Dictionary<string, ProtocolOutboxRow> rows = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => ids.Contains(row.MessageId))
            .ToDictionaryAsync(row => row.MessageId, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        for (int offset = 0; offset < ids.Length; offset++)
        {
            if (!rows.TryGetValue(ids[offset], out ProtocolOutboxRow? row))
            {
                return false;
            }
            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            JsonElement payload = document.RootElement.GetProperty("payload");
            if (payload.GetProperty("worklistRevision").GetInt64() != requested + offset ||
                !string.Equals(payload.GetProperty("stationId").GetString(), stop.StationId, StringComparison.Ordinal) ||
                payload.GetProperty("items").GetArrayLength() != 0)
            {
                return false;
            }
        }

        return await LoadsAtStopUnchangedSinceAsync(runtime, stop, rows[ids[0]].CreatedAt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="since"/>（请求那一版清单落进发件箱的时刻）之后，本停靠的已装集合没变，两个方向都比（<see cref="StaleWorklistWithoutSubstantiveChangeAsync"/> 第 4 条）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只用服务端自己的钟</b>（PR #743 审查建议 1）。装货的时刻取服务端收到那条 <c>OperationResult</c> 的时刻：收件箱那一行的 <c>ReceivedAt</c>
    /// （<c>ProtocolInbox</c> 的主键就是结果的 messageId，即 <c>OperationResults.ResultId</c>）。不取 <c>StationOperations.CommittedAt</c>：那是车报的
    /// <c>observedAt</c>，车上位机的钟，偏快时会误拒、偏慢时会漏拦（现场 agv01 曾快 1041 ms）。<c>OperationResults.ReceivedAt</c> 写入时填的也是
    /// <c>observedAt</c>，同样不能用。清单落库时刻 <paramref name="since"/> 与纠错行的 <c>CreatedAt</c> 都是服务端的 <c>timeProvider</c>。
    /// 没按版号判：版号只记本停靠做完了几条（装上与终结不分），也不记每版之间补发了几张空清单，从版号推不回「那一版时本站装了哪几条」；要逐条记装上时刻得加列。
    /// </para>
    /// <para>
    /// <b>多了</b>：本停靠此刻每一条 <c>LOADED</c> 的装货结果，服务端都在 <paramref name="since"/> 之前收到。找不到收件箱那一行也算没证明，拒。
    /// <b>少了</b>（审查建议 3）：本停靠上装过货（装货操作已 <c>Committed</c>）、此刻却不再是 <c>LOADED</c> 或已移除的，一律拒。它离开的时刻库里没有记，
    /// 分不清是在那一版之前还是之后，取偏安全的一侧：最坏是一次本可受理的出发答了过期，操作员再按一次。
    /// <b>纠错</b>：本停靠任一条需求（含已终结、已移除的）在 <paramref name="since"/> 之后开过装货纠错，拒——已装的放置可能被改过。
    /// </para>
    /// <para>
    /// 时刻比较在内存里做：SQLite 不能在库里比较 <c>DateTimeOffset</c>。
    /// </para>
    /// </remarks>
    private async Task<bool> LoadsAtStopUnchangedSinceAsync(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        JourneyDemandRow[] here = await dbContext.Set<JourneyDemandRow>().AsNoTracking()
            .Where(row => row.JourneyId == runtime.JourneyId && row.PickupStopId == stop.StopId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        string[] attempts = [.. here.Select(row => row.LoadSlotOperationAttemptId)];
        HashSet<string> committedLoads = new(
            await dbContext.StationOperations.AsNoTracking()
                .Where(row => attempts.Contains(row.SlotOperationAttemptId) && row.Status == StationOperationStatus.Committed)
                .Select(row => row.SlotOperationAttemptId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false),
            StringComparer.Ordinal);
        (string Attempt, string ResultId)[] results = [.. (await dbContext.OperationResults.AsNoTracking()
                .Where(row => attempts.Contains(row.SlotOperationAttemptId) && row.SupersededByResultId == null)
                .Select(row => new { row.SlotOperationAttemptId, row.ResultId })
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .Select(row => (row.SlotOperationAttemptId, row.ResultId))];
        string[] resultIds = [.. results.Select(result => result.ResultId)];
        Dictionary<string, DateTimeOffset> receivedAt = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => resultIds.Contains(row.MessageId))
            .ToDictionaryAsync(row => row.MessageId, row => row.ReceivedAt, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        foreach (JourneyDemandRow member in here)
        {
            bool loadedNow = member.Status == JourneyDemandStatuses.Loaded && member.RemovedAt is null;
            if (!loadedNow)
            {
                if (committedLoads.Contains(member.LoadSlotOperationAttemptId))
                {
                    return false;
                }
                continue;
            }
            string[] reported = [.. results.Where(result => result.Attempt == member.LoadSlotOperationAttemptId).Select(result => result.ResultId)];
            if (reported.Length == 0 ||
                reported.Any(id => !receivedAt.TryGetValue(id, out DateTimeOffset at) || at > since))
            {
                return false;
            }
        }

        string[] demandIds = [.. here.Select(row => row.DemandId)];
        return !(await dbContext.RecoveryWorkflows.AsNoTracking()
                .Where(row => row.DemandId != null && demandIds.Contains(row.DemandId) &&
                              row.WorkflowType == "LOAD_CORRECTION" && row.AgvId == runtime.AgvId)
                .Select(row => row.CreatedAt)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .Any(createdAt => createdAt > since);
    }

    /// <summary>锚需求还在这趟旅程上、没开始装。</summary>
    private static bool AnchorUnloaded(JourneyRuntimeRow runtime, JourneyStopCursor stops) =>
        stops.AllDemands.Any(item =>
            string.Equals(item.Demand.DemandId, runtime.DemandId, StringComparison.Ordinal) &&
            item.Demand.Status == DemandExecutionStatus.Accepted &&
            item.Membership.Status == JourneyDemandStatuses.PendingLoad);

    /// <summary>车上的篮数：已装上与正在装的需求的期待篮数之和（审计用）。</summary>
    private static int LoadedBaskets(JourneyStopCursor stops) =>
        stops.AllDemands
            .Where(item => item.Membership.Status is JourneyDemandStatuses.Loaded or JourneyDemandStatuses.Loading)
            .Sum(item => item.Membership.ExpectedBasketCount);

    /// <summary>保存之后发：出发的空清单与业务状态快照，或收尾的三张快照。送不到时留在发件箱，由补发送出。</summary>
    private async Task SendStagingDepartureAsync(
        JourneyRuntimeRow runtime,
        StagingDepartureEffect? effect,
        CancellationToken cancellationToken)
    {
        switch (effect)
        {
            case StagingDepartureEffect.JourneyClosed:
                await JourneyClosure.SendAsync(publisher, dbContext, runtime.AgvId, cancellationToken).ConfigureAwait(false);
                checkpointWaits.Clear(runtime.VehicleKey);
                break;
            case StagingDepartureEffect.StationWaitEnded:
                // 只结束站点离站等待：没有暂存的快照，离站核验由本轮下面的离站等待那一支发。
                break;
            case StagingDepartureEffect.Departing:
                JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
                JourneyStopRow current = stops.Current;
                await SendStoredAsync(
                        LoadingPhaseMessageId(current, StopRevision(runtime.VehicleBusinessRevision, current)), cancellationToken)
                    .ConfigureAwait(false);
                await StopEndWorklist.SendAsync(publisher, dbContext, runtime.AgvId, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>发一行已落库的报文；车此刻不在线上不算失败，留给补发（同 <see cref="JourneyClosure.SendAsync"/>）。</summary>
    private async Task SendStoredAsync(string messageId, CancellationToken cancellationToken)
    {
        try
        {
            await publisher.SendPersistedAsync(messageId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // 留在发件箱里，等补发。
        }
    }

    /// <summary>
    /// 这辆车此刻的电量低于这趟旅程冻结的那一版充电策略的强制充电线时返回读数与线；读不到、过期、没有冻结版本、不低于线都返回空
    /// （control-server#709 的读法，两支共用）。
    /// </summary>
    private async Task<(int Percent, int EntryThreshold)?> MandatoryChargeBatteryAsync(
        JourneyRuntimeRow runtime,
        CancellationToken cancellationToken)
    {
        if (runtime.ChargingPolicyVersion is not long policyVersion)
        {
            return null;
        }
        RiotVehicleObservation vehicle;
        try
        {
            vehicle = await vehicleFacts.ReadVehicleAsync(runtime.VehicleKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        // 读完之后另取一次时钟再比（control-server#709 独立审查 M1）：生产的 RIoT 网关在 HTTP 调用返回之后才给读数盖时间戳。
        DateTimeOffset readAt = timeProvider.GetUtcNow();
        if (vehicle.BatteryPercent is not int battery ||
            vehicle.ObservedAt > readAt ||
            readAt - vehicle.ObservedAt > runtimeOptions.MaximumEvidenceAge)
        {
            return null;
        }
        ChargingPolicyContent policy = (await chargingPolicy.ReadFrozenAsync(policyVersion, cancellationToken)
            .ConfigureAwait(false)).Content;
        return Dispatch.Criteria.BatteryEligibility.IsMandatoryCharge(battery, policy)
            ? (battery, policy.MandatoryChargeEntryThresholdPercent)
            : null;
    }

    /// <summary>
    /// 没有在途旅程的车上还没答的出发请求：答 <c>STATION_MISMATCH</c>（车不在任何停靠上）。引擎只推进在途旅程，一条请求到的时候旅程恰好收尾
    /// （或车本来就空着），没有这一支它就永远没人答，车载端会一直挂着（hmi#349 联调第 2 条）。只答会话就绪的车，与引擎同一道闸：
    /// 握手中间不插行。
    /// </summary>
    private async Task AnswerDepartureRequestsWithoutJourneyAsync(
        IReadOnlyCollection<JourneyRuntimeRow> active,
        CancellationToken cancellationToken)
    {
        // 空闲返回与充电旅程不答出发请求（它们的推进段不读它），所以那样的车与空车一样由这里答。
        HashSet<string> busy = active
            .Where(row => !row.IsIdleReturn() && !row.IsCharging())
            .Select(row => row.AgvId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var vehicle in roster.Vehicles)
        {
            if (busy.Contains(vehicle.AgvId))
            {
                continue;
            }
            IReadOnlyList<CurrentStopDepartures.Request> pending = await CurrentStopDepartures
                .UnansweredAsync(dbContext, vehicle.AgvId, cancellationToken).ConfigureAwait(false);
            if (pending.Count == 0 ||
                await CurrentReadySessionAsync(vehicle.AgvId, cancellationToken).ConfigureAwait(false) is not { } session)
            {
                continue;
            }
            List<string> answered = [];
            await using (IDbContextTransaction? transaction = dbContext.Database.CurrentTransaction is null
                             ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
                             : null)
            {
                // 写锁里再看一次：这一刻若刚有了旅程，留给它下一轮答。
                JourneyRuntimeRow[] open = await dbContext.JourneyRuntimes.AsNoTracking()
                    .Where(row => row.AgvId == vehicle.AgvId && row.Stage != JourneyRuntimeStage.Completed)
                    .ToArrayAsync(cancellationToken).ConfigureAwait(false);
                if (open.Any(row => !row.IsIdleReturn() && !row.IsCharging()))
                {
                    continue;
                }
                long revision = await CurrentStopDepartures.CurrentWorklistRevisionAsync(dbContext, vehicle.AgvId, cancellationToken)
                    .ConfigureAwait(false);
                DateTimeOffset now = timeProvider.GetUtcNow();
                foreach (CurrentStopDepartures.Request request in pending)
                {
                    WireProblem problem = CurrentStopDepartureRule.Decide(
                        request,
                        new CurrentStopDepartureRule.Facts(null, null, revision, false, false, false, false, false, false, false))!;
                    string resultId = CurrentStopDepartures.ResultMessageId(request.DepartureRequestId);
                    if (await OnboardJourneyPublisher.StageCurrentStopDepartureResultAsync(
                            new WireToGateStore(dbContext), resultId, vehicle.AgvId, session.SessionGeneration,
                            request.DepartureRequestId, problem, revision, now, cancellationToken).ConfigureAwait(false))
                    {
                        answered.Add(resultId);
                        LogDepartureAnswered(
                            logger, request.DepartureRequestId, request.OperatorId, vehicle.AgvId,
                            $"REJECTED ({problem.ReasonCode})", 0, 0, null);
                    }
                }
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            foreach (string resultId in answered)
            {
                await SendStoredAsync(resultId, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
