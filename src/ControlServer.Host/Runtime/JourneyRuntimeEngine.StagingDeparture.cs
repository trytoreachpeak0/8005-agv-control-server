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

    /// <summary>出发或缺电的放回落下了什么：旅程原地收尾，或带着已装的货进离站。</summary>
    private enum StagingDepartureEffect
    {
        JourneyClosed,
        Departing
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
                runtime, locked, session, stageNow.Value, cancellationToken).ConfigureAwait(false);
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
                (effect, released) = await StageStagingDepartureAsync(
                    runtime,
                    locked,
                    session,
                    holdingApplicable,
                    CurrentStopDepartures.OperatorDepartedReason,
                    LoadingClosedReasons.OperatorDeparted,
                    now.AddMilliseconds(1),
                    cancellationToken).ConfigureAwait(false);
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
            runtime.LoadingPhaseState == LoadingPhaseStates.Closed &&
            runtime.LoadingClosedReason is LoadingClosedReasons.OperatorDeparted or LoadingClosedReasons.ChargingRequired ||
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
                LogStagingLowBatteryAnchorUnloaded(logger, runtime.AgvId, null);
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

        // 装货阶段关闭，业务状态快照与它同一次改动（PublishLoadingPhaseAsync 的号与退役，只是暂存而不发）。车停在这个停靠上，按「已到站」算号。
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

        // 本站因此没有待做项了：空清单说出发或缺电（wire-notes 第 4 条）。本站本来就装完了（持货等单）时不再补一张：车上手里那张清单照旧，
        // 离站由业务状态快照与离站核验表达（CP-0012 第三节第 5 条由本票定，见 PR）。
        await StopEndWorklist.StageAfterReleaseAsync(
            dbContext, runtime, releasedFromCurrentStop > 0, sublotRequestMessageId, stopReason, at.AddMilliseconds(1),
            cancellationToken).ConfigureAwait(false);
        return (StagingDepartureEffect.Departing, leavingIds.Length);
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
            AlreadyDeparting: runtime.LoadingPhaseState == LoadingPhaseStates.Closed &&
                              runtime.LoadingClosedReason is LoadingClosedReasons.OperatorDeparted or LoadingClosedReasons.ChargingRequired,
            AnchorUnloadedWithCargo: loaded && AnchorUnloaded(runtime, locked),
            NothingLoadedAndDoorsNotProvenClosed: !loaded && SlotDoorsNotProvenClosed(session));
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
