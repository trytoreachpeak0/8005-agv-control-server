using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.Host.Runtime.Charging;

/// <summary>
/// 人工清桩确认之后、旧单已终结时的那一次释放（批次9-08，control-server#406）：周期结束、这一趟持有的桩独占释放、车的用途放开，<b>一个事务</b>。
/// 充不上之后的清桩中，清桩在这一刻才完成（<see cref="CompleteAndReleaseAsync"/>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>两个调用方，同一个删除条件</b>：人工确认（车载端或 Host，<see cref="ManualStationClearance"/>）在确认那一刻旧单已终结时就放；旧单那时还没收敛的，
/// 由引擎在对账到终态的那一轮放（<c>JourneyRuntimeEngine.UnableToCharge.cs</c>）。两者几乎同时到时只有一方生效：周期按读到的版本号更新、
/// 独占行按读到的持有旅程与经过删除，另一方更新到 0 行，整个事务回滚、什么也不写。
/// </para>
/// <para>
/// <b>不碰桩的分配暂停</b>：释放独占不等于解除暂停，暂停一直保持到 <c>ChargingStationRecoveryConfirmation</c>（票面第 6、8 条）。
/// <b>不收尾旅程</b>：旅程行由引擎在下一轮收尾、发收尾快照（它已经是 <see cref="ChargingExecutionReasons.ClearedEndings"/> 之一结束的周期），
/// 人工确认那条路从不写旅程行——那一行是引擎这一轮正在推进的那一行。
/// </para>
/// <para>
/// 周期已经结束的（失败周期留下、一直放不掉的预占）只放独占行，周期不再动。
/// </para>
/// </remarks>
public static class ChargerClearanceRelease
{
    /// <summary>
    /// 放开。答是否这一次放成了（另一方已经放过、或周期在读到之后被别处推进过，答假，什么也没写）。
    /// </summary>
    /// <remarks>
    /// 调用方已经开着事务时不另开：答假时前半可能已在那个事务里写下，调用方要回滚它（<see cref="ManualStationClearance"/> 就这么做）。
    /// </remarks>
    /// <param name="cycleVersion">读到周期时的版本号；周期已结束时不看。</param>
    /// <param name="releaseReason">写在桩独占经过上的释放原因；系统到等待点完成清桩时另给（control-server#409）。</param>
    public static async Task<bool> ReleaseAsync(
        ControlServerDbContext dbContext,
        string cycleId,
        long cycleVersion,
        string endReason,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        string releaseReason = ChargingExecutionReasons.ChargerReleasedOnManualClearance)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(cycleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endReason);

        ChargingCycleRow cycle = await dbContext.Set<ChargingCycleRow>().AsNoTracking()
            .SingleAsync(row => row.CycleId == cycleId, cancellationToken).ConfigureAwait(false);
        StationExclusivityRow? held = await dbContext.Set<StationExclusivityRow>().AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.MapId == cycle.MapId && row.StationId == cycle.StationId &&
                       row.StationKind == StationExclusivityKinds.Charger && row.JourneyId == cycle.JourneyId,
                cancellationToken)
            .ConfigureAwait(false);
        bool open = cycle.Phase != ChargingCyclePhases.Ended;
        if (!open && held is null)
        {
            return false;
        }

        IDbContextTransaction? transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        await using (transaction)
        {
            if (open)
            {
                int ended = await dbContext.Set<ChargingCycleRow>()
                    .Where(row => row.CycleId == cycleId && row.Version == cycleVersion && row.Phase != ChargingCyclePhases.Ended)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(row => row.Phase, ChargingCyclePhases.Ended)
                            .SetProperty(row => row.WireState, ChargingCycleWireStates.NotCharging)
                            .SetProperty(row => row.ReleasedAt, now)
                            .SetProperty(row => row.EndedAt, now)
                            .SetProperty(row => row.EndReason, endReason)
                            .SetProperty(row => row.Version, cycleVersion + 1),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (ended == 0)
                {
                    await RollBackAsync(transaction, cancellationToken).ConfigureAwait(false);
                    return false;
                }
            }

            if (held is not null &&
                !await FixedStationExclusivity.ReleaseAsReadAsync(
                        dbContext, held, now, releaseReason, cancellationToken)
                    .ConfigureAwait(false))
            {
                await RollBackAsync(transaction, cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (open)
            {
                await JourneyPurposeClaimRelease.StageAsync(dbContext, cycle.JourneyId, now, endReason, cancellationToken)
                    .ConfigureAwait(false);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // A tracked copy of the cycle reads as it was; the next read is fresh.
        foreach (var stale in dbContext.ChangeTracker.Entries<ChargingCycleRow>()
                     .Where(entry => entry.Entity.CycleId == cycleId).ToArray())
        {
            stale.State = EntityState.Detached;
        }
        return true;
    }

    /// <summary>
    /// 充不上之后的清桩中：人工确认已经记下（<see cref="StationClearanceRow.ConfirmedAt"/>）且旧单此刻已终结，<b>清桩在这一刻完成</b>——写
    /// <see cref="StationClearanceRow.CompletedAt"/>、证明与旧单的终态处置，并在同一个事务里放桩（<see cref="ReleaseAsync"/>）。答是否这一次完成了。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么完成要等两样</b>（control-server#406 独立审查）：cs#399 的模型里清桩完成就是 <c>CompletedAt</c>；<c>REQ-0178</c> 说旧单要先到取消终态、
    /// 取消结果未知时不完成清桩，<c>REQ-0179</c> 说人工确认是完成的证明。所以两样齐了才完成。人工确认先到、旧单后终结，由引擎在读到终态的那一轮完成；
    /// 旧单先终结、确认后到，由确认那一刻完成。两边调的都是这一个函数。
    /// </para>
    /// <para>
    /// <b>只有一方写成</b>：完成按「还没完成、确认已记下」更新，释放按读到的周期版本与持有者；任一步更新到 0 行，整个事务回滚、什么也不写，答假。
    /// 调用方已经开着事务时用它的（答假时调用方回滚）。
    /// </para>
    /// </remarks>
    public static async Task<bool> CompleteAndReleaseAsync(
        ControlServerDbContext dbContext,
        string clearanceId,
        string cycleId,
        long cycleVersion,
        string endReason,
        string settledDisposition,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(clearanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(settledDisposition);

        IDbContextTransaction? transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        await using (transaction)
        {
            int completed = await dbContext.Set<StationClearanceRow>()
                .Where(row => row.ClearanceId == clearanceId && row.CompletedAt == null && row.ConfirmedAt != null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.CompletedAt, now)
                        .SetProperty(row => row.Proof, StationClearanceProofs.ManualConfirmation)
                        .SetProperty(row => row.OldOrderDisposition, settledDisposition),
                    cancellationToken)
                .ConfigureAwait(false);
            if (completed == 0 ||
                !await ReleaseAsync(dbContext, cycleId, cycleVersion, endReason, now, cancellationToken).ConfigureAwait(false))
            {
                await RollBackAsync(transaction, cancellationToken).ConfigureAwait(false);
                ForgetClearance(dbContext, clearanceId);
                return false;
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        ForgetClearance(dbContext, clearanceId);
        return true;
    }

    /// <summary>变更跟踪里这条清桩记录的旧副本（更新绕过了它）。</summary>
    private static void ForgetClearance(ControlServerDbContext dbContext, string clearanceId)
    {
        foreach (var stale in dbContext.ChangeTracker.Entries<StationClearanceRow>()
                     .Where(entry => entry.Entity.ClearanceId == clearanceId).ToArray())
        {
            stale.State = EntityState.Detached;
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
