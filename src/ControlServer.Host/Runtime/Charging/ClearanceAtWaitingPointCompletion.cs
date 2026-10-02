using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Charging;

/// <summary>
/// 系统证明完成清桩（批次9-11，control-server#409，<c>REQ-0179</c>「系统确认车辆已到地图等待点即完成清桩」）的清桩那一半：写清桩记录的完成
/// （证明 <see cref="StationClearanceProofs.ArrivedAtWaitingPoint"/>、等待点、旧单的终态处置），并在同一个事务里放桩
/// （<see cref="ChargerClearanceRelease.ReleaseAsync"/>：周期结束、桩独占释放、用途放开）。
/// </summary>
/// <remarks>
/// <para>
/// <b>不开事务、不保存旅程</b>：调用方（引擎到点那一轮）开着事务，在同一个事务里还要把等待点预占转占用、旅程收尾，然后一起提交——
/// 「桩已空而清桩未完成」「清桩已完成而等待点仍是预占」都不会落库。这里任一步更新到 0 行就答假，调用方回滚。
/// </para>
/// <para>
/// <b>只有一方写成</b>（与人工清桩并发）：完成按「还没完成」更新、释放按读到的周期版本与持有者；人工确认在同一刻完成过，这里更新到 0 行，答假。
/// 已记下但还没完成的人工确认不挡它：系统证明照样完成，确认人那几列留着作记录。
/// </para>
/// <para>
/// <b>不碰桩的分配暂停</b>，与人工清桩一样：暂停只由 <c>ChargingStationRecoveryConfirmation</c> 解除。
/// </para>
/// <para>
/// <b>三项确认</b>（<c>REQ-0173</c>）在调用方判：车已证明静止停在等待点上（所以不在桩上、不在充电），桩被这一趟独占且暂停着，别的车不会被派去它。
/// </para>
/// </remarks>
internal static class ClearanceAtWaitingPointCompletion
{
    public static async Task<bool> CompleteAsync(
        ControlServerDbContext dbContext,
        string clearanceId,
        string cycleId,
        long cycleVersion,
        string settledDisposition,
        int waitingPointMapId,
        int waitingPointStationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(clearanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(settledDisposition);
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The system clearance completes inside the caller's transaction.");
        }

        int completed = await dbContext.Set<StationClearanceRow>()
            .Where(row => row.ClearanceId == clearanceId && row.CompletedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.CompletedAt, now)
                    .SetProperty(row => row.Proof, StationClearanceProofs.ArrivedAtWaitingPoint)
                    .SetProperty(row => row.WaitingPointMapId, waitingPointMapId)
                    .SetProperty(row => row.WaitingPointStationId, waitingPointStationId)
                    .SetProperty(row => row.OldOrderDisposition, settledDisposition),
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var stale in dbContext.ChangeTracker.Entries<StationClearanceRow>()
                     .Where(entry => entry.Entity.ClearanceId == clearanceId).ToArray())
        {
            stale.State = EntityState.Detached;
        }
        return completed == 1 &&
               await ChargerClearanceRelease.ReleaseAsync(
                       dbContext, cycleId, cycleVersion, ChargingExecutionReasons.UnableToChargeClearedAtWaitingPoint, now,
                       cancellationToken, ChargingExecutionReasons.ChargerReleasedOnClearanceAtWaitingPoint)
                   .ConfigureAwait(false);
    }
}
