using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>一次清桩开往等待点的承诺结果。</summary>
public enum ClearanceWaitingPointCommitOutcome
{
    /// <summary>预占、停靠与订单意图同一次保存落库。</summary>
    Committed,

    /// <summary>这辆车的用途占有已不是这趟旅程的 <c>CLEARING_MAINTENANCE</c>（被人工清桩放开了、或被别处改了）：什么也没写。</summary>
    ClaimNotHeld,

    /// <summary>等待点被别的承诺先拿到（站点独占的主键拒绝了这一次保存）：整笔回滚，什么也没写。</summary>
    StationHeld,
}

/// <summary>
/// 清桩中的车开往等待点的原子承诺（批次9-11，control-server#409，<c>REQ-0178</c>：多车原子预占）：同一次保存里取得等待点预占、写开往它的停靠与订单意图，
/// 并把原桩那个停靠标成完成——车要离桩了，旅程的当前停靠从此是等待点那一个。
/// </summary>
/// <remarks>
/// <para>
/// <b>用途不新取</b>：车已经持有这趟旅程的 <c>CLEARING_MAINTENANCE</c>，保持它。保存之前在同一个事务里按「这辆车、这趟旅程、这个用途」更新一次占有行
/// （不改值），更新到 0 行就是占有已不在（人工清桩在这一刻完成并放开了它），回滚、什么也不写。那一次更新也拿到了库的写锁，所以之后到提交为止，
/// 别的上下文放不开这条占有——「先读后写」之间的空档不存在。
/// </para>
/// <para>
/// <b>输赢由主键决定</b>：等待点由 <c>StationExclusivities</c> 的主键仲裁，与空闲返回、充电离桩去等待点是同一张表、同一个键（<c>REQ-0178</c>：同一个等待点集合）。
/// 被拒时整笔回滚，暂存的行全部丢掉、原桩停靠的状态改回去，调用方本轮不换点重试，下一轮重评。
/// </para>
/// <para>
/// 调用方已经开着事务时用它的（答非 <see cref="ClearanceWaitingPointCommitOutcome.Committed"/> 时调用方回滚）。
/// </para>
/// </remarks>
public static class ClearanceWaitingPointCommitment
{
    public static async Task<ClearanceWaitingPointCommitOutcome> TryCommitAsync(
        ControlServerDbContext dbContext,
        string vehicleKey,
        string journeyId,
        int mapId,
        int stationId,
        long waitingPointVersion,
        JourneyStopRow waitingPointStop,
        OrderIntent intent,
        JourneyStopRow chargerStop,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        ArgumentNullException.ThrowIfNull(waitingPointStop);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(chargerStop);

        StationExclusivityRequest request = new(
            mapId, stationId, StationExclusivityKinds.WaitingPoint, StationExclusivityStates.Reserved, waitingPointVersion);
        StationExclusivityWrites.Validate(request);

        IDbContextTransaction? transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        string chargerStatus = chargerStop.Status;
        List<object> staged = [];
        await using (transaction)
        {
            int held = await dbContext.Set<VehiclePurposeClaimRow>()
                .Where(row => row.VehicleKey == vehicleKey && row.JourneyId == journeyId &&
                              row.Purpose == VehiclePurposes.ClearingMaintenance)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ClaimedAt, row => row.ClaimedAt), cancellationToken)
                .ConfigureAwait(false);
            if (held == 0)
            {
                await RollBackAsync(transaction, cancellationToken).ConfigureAwait(false);
                return ClearanceWaitingPointCommitOutcome.ClaimNotHeld;
            }

            StationExclusivityWrites.ForgetUnchangedStation(dbContext, mapId, stationId);
            staged.AddRange(StationExclusivityWrites.NewRows(request, vehicleKey, journeyId, at));
            staged.Add(waitingPointStop);
            staged.Add(WireToGateStore.ToRow(intent));
            dbContext.AddRange(staged);
            chargerStop.Status = JourneyStopStatuses.Completed;
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException failure) when (StationExclusivityWrites.IsKeyConflict(failure))
            {
                await RollBackAsync(transaction, cancellationToken).ConfigureAwait(false);
                Forget(dbContext, staged, chargerStop, chargerStatus);
                return ClearanceWaitingPointCommitOutcome.StationHeld;
            }
            catch
            {
                // Anything else (the database busy, the connection gone): nothing of it may ride on the round's next save.
                Forget(dbContext, staged, chargerStop, chargerStatus);
                throw;
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        return ClearanceWaitingPointCommitOutcome.Committed;
    }

    private static void Forget(ControlServerDbContext dbContext, List<object> staged, JourneyStopRow chargerStop, string chargerStatus)
    {
        foreach (object row in staged)
        {
            dbContext.Entry(row).State = EntityState.Detached;
        }
        chargerStop.Status = chargerStatus;
        if (dbContext.Entry(chargerStop).State == EntityState.Modified)
        {
            dbContext.Entry(chargerStop).State = EntityState.Unchanged;
        }
    }

    private static async Task RollBackAsync(IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
