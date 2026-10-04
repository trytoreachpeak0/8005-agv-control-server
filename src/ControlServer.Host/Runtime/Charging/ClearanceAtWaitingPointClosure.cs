using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Charging;

/// <summary>
/// 清桩开往等待点到了点之后的收尾那一半（批次9-11，control-server#409）：等待点预占转占用、两个停靠标完成、用途放开、被取代的途中快照退役、
/// 旅程收尾并暂存收尾快照。暂存、不保存。
/// </summary>
/// <remarks>
/// <para>
/// 从引擎的到点完成里抽出来（control-server#447），因为它有两个调用方：引擎到点那一轮（清桩由系统证明完成，或人工清桩已先完成），与等待点到点人工收尾
/// （<see cref="WaitingPointArrivalSettlement"/>，人确认车就在点上、清桩已由人工确认完成）。两边走同一份记账，不各抄一份。
/// </para>
/// <para>
/// 清桩记录的完成与放桩不在这里（<see cref="ClearanceAtWaitingPointCompletion"/>，只有系统证明那一支要做）；调用方开着事务时，这一半与它同一次提交。
/// </para>
/// </remarks>
internal static class ClearanceAtWaitingPointClosure
{
    /// <summary>
    /// 暂存收尾；等待点此刻已不归这一趟时什么也不暂存，答假（调用方回滚或拒绝）。
    /// </summary>
    public static async Task<bool> StageAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        JourneyStopRow charger,
        JourneyStopRow move,
        string ending,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(charger);
        ArgumentNullException.ThrowIfNull(move);
        ArgumentException.ThrowIfNullOrWhiteSpace(ending);

        StationExclusivityRow? held = await WaitingPointExclusivity
            .HeldByAsync(dbContext, runtime.MapId, move.StationRiotId, runtime.JourneyId, cancellationToken)
            .ConfigureAwait(false);
        if (held is null)
        {
            return false;
        }
        await WaitingPointExclusivity.StageOccupyAsync(dbContext, held, now, cancellationToken).ConfigureAwait(false);
        move.Status = JourneyStopStatuses.Completed;
        charger.Status = JourneyStopStatuses.Completed;
        await JourneyPurposeClaimRelease.StageAsync(dbContext, runtime.JourneyId, now, ending, cancellationToken).ConfigureAwait(false);
        foreach (string superseded in JourneyRuntimeEngine.ChargingSnapshotIds(runtime)
                     .Concat(JourneyRuntimeEngine.ClearingSnapshotIds(runtime))
                     .Concat(ClearanceMoveShape.SnapshotIds(runtime.JourneyId, ClearanceMoveShape.AttemptOf(move))))
        {
            ProtocolOutboxRow? row = await dbContext.ProtocolOutbox
                .SingleOrDefaultAsync(
                    item => item.MessageId == superseded && item.AcknowledgedAt == null && item.FencedAt == null, cancellationToken)
                .ConfigureAwait(false);
            if (row is not null)
            {
                row.FencedAt = now;
            }
        }
        await JourneyClosure.StageClearanceAtWaitingPointAsync(dbContext, runtime, move, ending, now, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 清桩已由人工确认完成之后（周期已结束）收尾用的码：周期结束时写下的那一个清桩收尾码，读不到时 <see cref="ChargingExecutionReasons.UnableToChargeCleared"/>。
    /// </summary>
    public static async Task<string> EndingAfterClearedCycleAsync(
        ControlServerDbContext dbContext, string journeyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        string?[] reasons = await dbContext.Set<ChargingCycleRow>().AsNoTracking()
            .Where(row => row.JourneyId == journeyId)
            .Select(row => row.EndReason)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return reasons.SingleOrDefault(reason => reason is not null && ChargingExecutionReasons.ClearedEndings.Contains(reason))
               ?? ChargingExecutionReasons.UnableToChargeCleared;
    }
}
