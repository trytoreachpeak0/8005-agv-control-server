using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Charging;

/// <summary>
/// 一次清桩开往等待点没到点就结束（批次9-11，control-server#409）：暂存、不保存，调用方与它自己的事实同一次保存。
/// </summary>
/// <remarks>
/// <para>
/// <b>同一次保存里</b>：那个停靠标成移除、原桩停靠回到待定（旅程的当前停靠回到原桩，人工清桩与故障恢复读的都是它）、给了原因时这一趟持有的等待点预占
/// 当场释放并把原因写进它的经过、途中两张快照退役，周期仍是清桩中时再暂存两张「回到清桩中」的快照（原桩腿 <c>ARRIVED</c>，车载端靠它取原桩站点号）。
/// </para>
/// <para>
/// <b>原因决定之后</b>（<see cref="ClearanceMoveReleaseReasons"/>）：撤回不留痕迹，查无此单排除这个点，被人结束则这个周期不再自动出发。
/// 车可能动过的结束只在证明停稳没单之后才调这里——那是调用方的事。
/// </para>
/// <para>
/// 两个调用方：引擎的清桩移动分支，与人工清除故障（<c>VehicleFaultRecoveryService</c>，FAILED 的清桩移动单由人清除后，原因是
/// <see cref="ClearanceMoveReleaseReasons.Ended"/>）。
/// </para>
/// </remarks>
internal static class ClearanceMoveEnding
{
    /// <summary>暂存这次结束；答是否暂存了那两张「回到清桩中」的快照（周期已被人工清桩结束、或车没有会话时没有，发送方据此不去发）。</summary>
    public static async Task<bool> StageAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        JourneyStopRow move,
        string? releaseReason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(move);

        move.Status = JourneyStopStatuses.Removed;
        JourneyStopRow charger = await dbContext.Set<JourneyStopRow>()
            .SingleAsync(row => row.JourneyId == runtime.JourneyId && row.StopRole == JourneyStopRoles.Charger, cancellationToken)
            .ConfigureAwait(false);
        charger.Status = JourneyStopStatuses.Pending;

        StationExclusivityRow? held = await WaitingPointExclusivity
            .HeldByAsync(dbContext, runtime.MapId, move.StationRiotId, runtime.JourneyId, cancellationToken)
            .ConfigureAwait(false);
        if (held is not null && releaseReason is not null)
        {
            await WaitingPointExclusivity.StageReleaseAsync(dbContext, held, now, releaseReason, cancellationToken)
                .ConfigureAwait(false);
        }

        int attempt = ClearanceMoveShape.AttemptOf(move);
        foreach (string superseded in new[]
                 {
                     ClearanceMoveShape.PlanMessageId(runtime.JourneyId, attempt),
                     ClearanceMoveShape.StateMessageId(runtime.JourneyId, attempt),
                 })
        {
            ProtocolOutboxRow? row = await dbContext.ProtocolOutbox
                .SingleOrDefaultAsync(item => item.MessageId == superseded && item.AcknowledgedAt == null && item.FencedAt == null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (row is not null)
            {
                row.FencedAt = now;
            }
        }

        bool clearing = await dbContext.Set<ChargingCycleRow>().AsNoTracking()
            .AnyAsync(row => row.JourneyId == runtime.JourneyId && row.Phase == ChargingCyclePhases.Clearing, cancellationToken)
            .ConfigureAwait(false);
        SessionRecoveryRow? session = await dbContext.SessionRecoveries.AsNoTracking()
            .SingleOrDefaultAsync(row => row.AgvId == runtime.AgvId, cancellationToken).ConfigureAwait(false);
        if (!clearing || session is null)
        {
            // Ended by a manual clearance meanwhile: the journey closes next round and its closure tells the vehicle.
            return false;
        }

        WireToGateStore store = new(dbContext);
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
            ClearanceMoveShape.BackPlanMessageId(runtime.JourneyId, attempt),
            runtime.AgvId,
            session.SessionGeneration,
            new UpcomingStopPlanProjection(planRevision, [JourneyPlanBuilder.ChargerLeg(runtime, charger, "ARRIVED")]),
            now,
            cancellationToken).ConfigureAwait(false);
        await OnboardJourneyPublisher.StageVehicleBusinessStateAsync(
            store,
            ClearanceMoveShape.BackStateMessageId(runtime.JourneyId, attempt),
            runtime.AgvId,
            session.SessionGeneration,
            new VehicleBusinessProjection(
                stateRevision, "READY", VehicleActivePurposes.ClearingMaintenance, false,
                JourneyRuntimeEngine.PublishedBatteryState(runtime), ChargingCycleWireStates.UnableToCharge, null, []),
            // A millisecond after the plan, so a replay -- which sends in creation order -- sends the plan first too.
            now.AddMilliseconds(1),
            cancellationToken).ConfigureAwait(false);
        return true;
    }
}
