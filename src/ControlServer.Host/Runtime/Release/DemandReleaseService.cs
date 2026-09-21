using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Release;

/// <summary>一次释放尝试的结果，给日志与测试看。</summary>
public sealed record DemandReleaseOutcome(string JourneyId, string DemandId, string Trigger, string Result);

/// <summary>
/// REQ-0328：仅当前车不合格时释放改派（集合 B，仅限未取货；批次7-10，control-server#215）。
/// </summary>
/// <remarks>
/// <para>
/// <b>一轮做三件事</b>：对每趟没结束的旅程、每条还没取货的需求问一次「这辆车对它还合不合格」
/// （<see cref="DemandReleaseRules.VehicleNoLongerEligible"/>）；不合格就裁决能不能释放（<see cref="DemandReleaseRules.Decide"/>）；
/// 能就先确认它的取货腿在 RIoT 上没有订单，再在一个写事务里释放、退回积压、修订计划。
/// </para>
/// <para>
/// <b>「没有订单」在写事务里判，不用这一轮开头的读数</b>（票面点名的并发读改写）。车正开往它的取货站时，那张单先经订单命令面
/// 取消并对账——命令面自己先武装审计行再发（<see cref="RiotOrderCommandService"/>），所以取消发出而释放没落库时崩溃，
/// 下一轮读到那条审计行就只对账、不再发第二次取消。<b>结果不是 Confirmed 就不释放</b>：Pending 下一轮再对账，
/// Failed 与 Unknown 写阻断原因，需求与车保持原状。取消必须在暂存任何改动之前发：审计存储自己保存，会把上下文里暂存的一并提交。
/// </para>
/// <para>
/// <b>释放与退回积压同一事务</b>：归属标移除、积压行清掉受理标记（<see cref="JourneyBacklogRow.FirstSeenAt"/> 不动，等待年龄因此
/// 保留）、计划修订暂存，一次保存。旅程上没有别的未结需求时按批次7-02 关闭旅程与三套占用
/// （<see cref="PickupStopTermination.StageJourneyClosureAsync(JourneyRuntimeRow, string, DateTimeOffset, CancellationToken)"/>）。
/// 仓位不用单独归还：占用由 <c>JourneyAwareSlotLedger</c> 按未移除的归属算。
/// </para>
/// <para>
/// <b>到站已记录则不释放</b>，在写事务里再判一次：取货站到站与释放同时发生时，到站那一侧先提交的话这里读到的已经不是
/// 「开往取货站」了。
/// </para>
/// </remarks>
public sealed class DemandReleaseService(
    ControlServerDbContext dbContext,
    IVehicleFaultStore faults,
    VehicleDispatchPolicyAccess dispatchPolicy,
    IRiotVehicleFacts vehicleFacts,
    RiotOrderCommandService orderCommands,
    IRiotOrderCommandAuditStore commandAudit,
    PlanRevisionRoutingSource routingSource,
    IOptions<JourneyRuntimeOptions> options,
    TimeProvider timeProvider,
    ILogger<DemandReleaseService> logger)
{
    public const string CancelCommandType = "CANCEL";

    private static readonly Action<ILogger, string, string, string, Exception?> LogReleased =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(2150, nameof(LogReleased)),
            "Demand {DemandId} was released from journey {JourneyId} for redispatch: the vehicle is no longer eligible ({Trigger}).");

    private static readonly Action<ILogger, string, string, string, string, Exception?> LogRefused =
        LoggerMessage.Define<string, string, string, string>(
            LogLevel.Warning,
            new EventId(2151, nameof(LogRefused)),
            "Demand {DemandId} of journey {JourneyId} was not released although the vehicle is no longer eligible ({Trigger}): {Reason}.");

    private static readonly Action<ILogger, string, string, Exception?> LogVehicleUnread =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2152, nameof(LogVehicleUnread)),
            "The RIoT observation of vehicle {VehicleKey} (journey {JourneyId}) could not be read; the map is not judged this round.");

    private readonly JourneyRuntimeOptions _options = options.Value;

    public async Task<IReadOnlyList<DemandReleaseOutcome>> RunOnceAsync(CancellationToken cancellationToken)
    {
        VehicleDispatchPolicy policy = await dispatchPolicy.EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
        JourneyRuntimeRow[] journeys = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.Stage != JourneyRuntimeStage.Completed)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        List<DemandReleaseOutcome> outcomes = [];
        foreach (JourneyRuntimeRow journey in journeys)
        {
            outcomes.AddRange(await RunForJourneyAsync(journey, policy, cancellationToken).ConfigureAwait(false));
        }

        return outcomes;
    }

    private async Task<IReadOnlyList<DemandReleaseOutcome>> RunForJourneyAsync(
        JourneyRuntimeRow journey,
        VehicleDispatchPolicy policy,
        CancellationToken cancellationToken)
    {
        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, journey, cancellationToken).ConfigureAwait(false);
        JourneyStopDemand[] waiting = [.. stops.Demands.Where(item =>
            item.Membership.Status == JourneyDemandStatuses.PendingLoad)];
        if (waiting.Length == 0)
        {
            return [];
        }

        VehicleFaultFact? fault = await faults.ReadAsync(journey.AgvId, cancellationToken).ConfigureAwait(false);
        RiotVehicleObservation? observation = await ReadVehicleAsync(journey, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        List<DemandReleaseOutcome> outcomes = [];
        foreach (JourneyStopDemand item in waiting)
        {
            string? trigger = DemandReleaseRules.VehicleNoLongerEligible(
                fault, policy, journey.AgvId, item.Demand.WorkType, item.Membership.DispatchZone, observation, now, _options);
            if (trigger is null)
            {
                continue;
            }

            outcomes.Add(await ReleaseOneAsync(journey, stops, item, trigger, cancellationToken).ConfigureAwait(false));
            // 释放改了这趟旅程的停靠与归属：下一条要按新的样子判，而不是按这一轮开头读到的。
            stops = await JourneyStopCursor.LoadAsync(dbContext, journey, cancellationToken).ConfigureAwait(false);
        }

        return outcomes;
    }

    private async Task<DemandReleaseOutcome> ReleaseOneAsync(
        JourneyRuntimeRow journey,
        JourneyStopCursor stops,
        JourneyStopDemand item,
        string trigger,
        CancellationToken cancellationToken)
    {
        string demandId = item.Membership.DemandId;
        bool pickupIsCurrent = string.Equals(stops.Current.StopId, item.Membership.PickupStopId, StringComparison.Ordinal);
        DemandReleaseDecision decision = DemandReleaseRules.Decide(
            item.Membership.Status,
            isAnchor: string.Equals(journey.DemandId, demandId, StringComparison.Ordinal),
            otherOpenDemands: stops.Demands.Count(other =>
                !string.Equals(other.Membership.DemandId, demandId, StringComparison.Ordinal)),
            pickupIsCurrentNextStop: pickupIsCurrent,
            arrivedAtCurrentNextStop: journey.Stage != JourneyRuntimeStage.AwaitingPickupArrival);

        switch (decision.Action)
        {
            case DemandReleaseAction.Refuse:
                return await RefuseAsync(journey, demandId, trigger, decision.RefusalReason!, cancellationToken)
                    .ConfigureAwait(false);
            case DemandReleaseAction.CancelPickupOrderThenRelease:
                string? notCancelled = await CancelPickupOrderAsync(journey, stops.Current, cancellationToken)
                    .ConfigureAwait(false);
                if (notCancelled is not null)
                {
                    return await RefuseAsync(journey, demandId, trigger, notCancelled, cancellationToken)
                        .ConfigureAwait(false);
                }

                return await ReleaseAsync(journey, demandId, trigger, pickupOrderCancelled: true, cancellationToken)
                    .ConfigureAwait(false);
            case DemandReleaseAction.ReleaseWithoutOrder:
                return await ReleaseAsync(journey, demandId, trigger, pickupOrderCancelled: false, cancellationToken)
                    .ConfigureAwait(false);
            default:
                return new DemandReleaseOutcome(journey.JourneyId, demandId, trigger, "NOT_APPLICABLE");
        }
    }

    /// <summary>
    /// 取消这趟旅程开往当前下一站的那张单并对账；确认取消了返回空，否则返回不释放的原因。
    /// </summary>
    /// <remarks>
    /// 先看有没有取消过：审计里有这张单的取消尝试就只对账那一次，不发第二次（崩溃点：取消已发出而释放未落库）。
    /// 这张单根本没建（意图不在或没有 RIoT 订单号）时没有东西可取消，照样当作「没有订单」。
    /// </remarks>
    private async Task<string?> CancelPickupOrderAsync(
        JourneyRuntimeRow journey,
        JourneyStopRow currentStop,
        CancellationToken cancellationToken)
    {
        OrderIntentRow? intent = await dbContext.OrderIntents.AsNoTracking()
            .SingleOrDefaultAsync(row => row.UpperId == currentStop.UpperId, cancellationToken).ConfigureAwait(false);
        if (intent?.OrderId is not { } orderId)
        {
            return null;
        }

        IReadOnlyList<RiotOrderCommandAttempt> attempts = await commandAudit
            .ReadAttemptsAsync(CancelCommandType, currentStop.UpperId, cancellationToken).ConfigureAwait(false);
        RiotOrderCommandOutcome outcome = attempts.Count > 0
            ? await orderCommands.ReconcileAsync(attempts[^1], cancellationToken).ConfigureAwait(false)
            : (await orderCommands.IssueAsync(
                    RiotOrderCommandKind.Cancel,
                    new RiotOrderCommandTarget(journey.AgvId, currentStop.UpperId, orderId),
                    "REQ-0328 release for redispatch: the vehicle is no longer eligible for this demand",
                    faultGeneration: null,
                    cancellationToken).ConfigureAwait(false)).Outcome;
        return outcome == RiotOrderCommandOutcome.Confirmed ? null : DemandReleaseReasons.OrderCancelNotConfirmed;
    }

    private async Task<DemandReleaseOutcome> ReleaseAsync(
        JourneyRuntimeRow journey,
        string demandId,
        string trigger,
        bool pickupOrderCancelled,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using IDbContextTransaction? transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        dbContext.ChangeTracker.Clear();
        JourneyRuntimeRow runtime = await dbContext.JourneyRuntimes
            .SingleAsync(row => row.JourneyId == journey.JourneyId, cancellationToken).ConfigureAwait(false);
        JourneyDemandRow? membership = await dbContext.Set<JourneyDemandRow>()
            .SingleOrDefaultAsync(
                row => row.JourneyId == journey.JourneyId && row.DemandId == demandId && row.RemovedAt == null,
                cancellationToken).ConfigureAwait(false);
        // 这一轮开头读完之后它可能已经装上了（集合 A）、被终结了，或者旅程已经结束：都不再是本条的事。
        if (membership is null || membership.Status != JourneyDemandStatuses.PendingLoad ||
            runtime.Stage == JourneyRuntimeStage.Completed)
        {
            return new DemandReleaseOutcome(journey.JourneyId, demandId, trigger, "NO_LONGER_APPLICABLE");
        }

        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
        JourneyStopRow pickup = stops.Stops.Single(row => row.StopId == membership.PickupStopId);
        if (string.Equals(stops.Current.StopId, pickup.StopId, StringComparison.Ordinal))
        {
            // 到站已记录则不释放；开往它的那张单也必须是刚刚确认取消的那一张。
            if (runtime.Stage != JourneyRuntimeStage.AwaitingPickupArrival)
            {
                return await RefuseInTransactionAsync(runtime, demandId, trigger, DemandReleaseReasons.AfterArrival, transaction,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!pickupOrderCancelled && await PickupOrderExistsAsync(pickup, cancellationToken).ConfigureAwait(false))
            {
                return await RefuseInTransactionAsync(runtime, demandId, trigger, DemandReleaseReasons.PickupOrderAppeared,
                    transaction, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (await PickupOrderExistsAsync(pickup, cancellationToken).ConfigureAwait(false))
        {
            return await RefuseInTransactionAsync(runtime, demandId, trigger, DemandReleaseReasons.PickupOrderAppeared,
                transaction, cancellationToken).ConfigureAwait(false);
        }

        bool last = await DemandJourneyLookup.IsLastOpenDemandAsync(dbContext, runtime.JourneyId, demandId, cancellationToken)
            .ConfigureAwait(false);
        if (last)
        {
            // 车上没有别的需求了：计划收尾（它的停靠连同当前下一站一起删），旅程按批次7-02 关闭、三套占用一起放。
            await JourneyPlanRevisionStage.StageAsync(
                dbContext, runtime.JourneyId, [demandId], currentStopMayGo: true, routing: null, cancellationToken)
                .ConfigureAwait(false);
            await new PickupStopTermination(dbContext)
                .StageJourneyClosureAsync(runtime, DemandReleaseReasons.Released, now, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await JourneyPlanRevisionStage.StageAsync(
                dbContext, runtime.JourneyId, [demandId], currentStopMayGo: false,
                await routingSource.ReadAsync(cancellationToken).ConfigureAwait(false), cancellationToken)
                .ConfigureAwait(false);
        }

        membership.RemovedAt = now;
        membership.RemovalReason = DemandReleaseReasons.Released;
        JourneyBacklogRow? backlog = await dbContext.JourneyBacklog
            .SingleOrDefaultAsync(row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
        if (backlog is not null)
        {
            // FirstSeenAt 不动：等待年龄从它算，释放不让一条需求重新排到队尾。
            backlog.AcceptedAt = null;
            backlog.ReasonCode = DemandReleaseReasons.Released;
            backlog.LastSeenAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        LogReleased(logger, demandId, runtime.JourneyId, trigger, null);
        return new DemandReleaseOutcome(runtime.JourneyId, demandId, trigger, "RELEASED");
    }

    /// <summary>这个取货停靠有没有 RIoT 订单意图：追加的停靠要到车离开上一站才建单，所以有就是「这一轮开头之后刚建的」。</summary>
    private Task<bool> PickupOrderExistsAsync(JourneyStopRow pickup, CancellationToken cancellationToken) =>
        dbContext.OrderIntents.AsNoTracking().AnyAsync(row => row.UpperId == pickup.UpperId, cancellationToken);

    private async Task<DemandReleaseOutcome> RefuseAsync(
        JourneyRuntimeRow journey,
        string demandId,
        string trigger,
        string reason,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        JourneyRuntimeRow runtime = await dbContext.JourneyRuntimes
            .SingleAsync(row => row.JourneyId == journey.JourneyId, cancellationToken).ConfigureAwait(false);
        return await RefuseInTransactionAsync(runtime, demandId, trigger, reason, transaction: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 不释放，写下原因。<b>只在旅程此刻没有阻断原因时写</b>：已有的原因（故障、检查点、会话）比「没能释放」更要紧，
    /// 不能被它盖掉；引擎清掉之后，下一轮仍不合格就再写一次。
    /// </summary>
    private async Task<DemandReleaseOutcome> RefuseInTransactionAsync(
        JourneyRuntimeRow runtime,
        string demandId,
        string trigger,
        string reason,
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (runtime.BlockReasonCode is null)
        {
            runtime.SetBlockReason(reason, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        LogRefused(logger, demandId, runtime.JourneyId, trigger, reason, null);
        return new DemandReleaseOutcome(runtime.JourneyId, demandId, trigger, reason);
    }

    private async Task<RiotVehicleObservation?> ReadVehicleAsync(JourneyRuntimeRow journey, CancellationToken cancellationToken)
    {
        try
        {
            return await vehicleFacts.ReadVehicleAsync(journey.VehicleKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            LogVehicleUnread(logger, journey.VehicleKey, journey.JourneyId, error);
            return null;
        }
    }
}
