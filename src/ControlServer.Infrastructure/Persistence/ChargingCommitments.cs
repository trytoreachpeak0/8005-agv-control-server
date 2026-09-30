using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 充电的原子承诺（<c>REQ-0173</c>；批次9-06，control-server#404）：周期行、<c>CHARGING</c> 用途占有、<c>CHARGER</c> 预占、承载它的旅程行与
/// 停靠、待建的订单意图，同一次保存。
/// </summary>
/// <remarks>
/// <para>
/// <b>输赢只由数据库约束决定，不先读后写。</b>车由 <c>VehiclePurposeClaims</c> 的主键仲裁，桩由 <c>StationExclusivities</c> 的主键仲裁，
/// 一车一个未结束的周期由过滤唯一索引仲裁——都在 <see cref="IChargingCycleStore.TryStartAsync"/> 那一次保存里。旅程行、停靠与意图
/// 经它的 <c>sameSave</c> 同去同回：任一键拒绝，六样一样不留。
/// </para>
/// <para>
/// <b>按车的快照修订号计数器</b>与受理搬运、物化空闲返回时一样，在同一次保存里推进（车载端按消息类型记修订号，充电发出的计划与业务状态
/// 必须接在上一趟之后）。它是一行<b>修改</b>而不是新增，进不了 <c>sameSave</c>，所以被拒之后由这里把它从变更跟踪里撤出：留着的话，
/// 调用方下一次保存会把一个没有旅程对应的基准写下去。
/// </para>
/// </remarks>
public static class ChargingCommitment
{
    /// <summary>
    /// 形成一次充电承诺。不是 <see cref="ChargingCycleStartOutcome.Started"/> 就是什么也没写、变更跟踪里什么也没留下。
    /// </summary>
    public static async Task<ChargingCycleStartOutcome> TryCommitAsync(
        ControlServerDbContext dbContext,
        IChargingCycleStore cycles,
        ChargingCycleStart start,
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        OrderIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(cycles);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentNullException.ThrowIfNull(intent);
        if (!runtime.IsCharging() || runtime.JourneyId != start.JourneyId || stop.JourneyId != start.JourneyId)
        {
            throw new ArgumentException(
                $"Journey '{runtime.JourneyId}' is not the charging journey of cycle '{start.CycleId}'.", nameof(runtime));
        }

        WireToGateStore store = new(dbContext);
        bool started = false;
        try
        {
            await store.SeedSnapshotRevisionsAsync(runtime, cancellationToken).ConfigureAwait(false);
            await store.AdvanceSnapshotRevisionCounterAsync(runtime, cancellationToken).ConfigureAwait(false);
            ChargingCycleStartOutcome outcome = await cycles
                .TryStartAsync(start, [runtime, stop, WireToGateStore.ToRow(intent)], cancellationToken).ConfigureAwait(false);
            started = outcome == ChargingCycleStartOutcome.Started;
            return outcome;
        }
        finally
        {
            if (!started)
            {
                Forget(dbContext, runtime.AgvId);
            }
        }
    }

    /// <summary>
    /// 把一次没成的承诺留在变更跟踪里的东西撤出去：周期存储在键冲突时自己撤它暂存的行，别的失败（库忙、连接断、注入的崩溃）不撤，
    /// 按车计数器那一行两种情况都不归它管。留着的话，轮次后面任何一次保存都会把半截承诺写下去。
    /// </summary>
    public static void Forget(ControlServerDbContext dbContext, string agvId)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        foreach (EntityEntry entry in dbContext.ChangeTracker.Entries()
                     .Where(entry => entry.State == EntityState.Added &&
                                     entry.Entity is ChargingCycleRow or VehiclePurposeClaimRow or VehiclePurposeClaimRecordRow
                                         or StationExclusivityRow or StationExclusivityRecordRow or JourneyRuntimeRow
                                         or JourneyStopRow or OrderIntentRow)
                     .ToArray())
        {
            entry.State = EntityState.Detached;
        }
        foreach (EntityEntry<VehicleSnapshotRevisionRow> counter in dbContext.ChangeTracker.Entries<VehicleSnapshotRevisionRow>()
                     .Where(entry => entry.Entity.AgvId == agvId &&
                                     entry.State is EntityState.Added or EntityState.Modified)
                     .ToArray())
        {
            counter.State = EntityState.Detached;
        }
    }
}

/// <summary>服务端持有的人工充电等待，在调用方自己的那一次保存里解除（批次9-06，control-server#404）。</summary>
public static class ManualChargingHoldWrites
{
    /// <summary>
    /// 暂存解除：删掉这辆车的当前行，在它的经过上写解除时刻与请求 id。车不在等待中时什么也不做，返回假。调用方保存。
    /// </summary>
    /// <remarks>
    /// 给「充电后返回服务」的判定用：判定行与解除必须同一次保存，要么都在、要么都不在（<c>CV-MANUAL-CHARGING-RETURN</c> 的
    /// <c>REEVALUATE_ELIGIBILITY_AFTER_RETURN</c>）。<see cref="IManualChargingHoldStore.ReleaseAsync"/> 自己保存，做不到这一点。
    /// </remarks>
    public static async Task<bool> StageReleaseAsync(
        ControlServerDbContext dbContext,
        string vehicleKey,
        string releaseRequestId,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseRequestId);
        // A row this context saw earlier may since have been released, or released and placed again, through another one.
        foreach (EntityEntry<ManualChargingHoldRow> stale in dbContext.ChangeTracker.Entries<ManualChargingHoldRow>()
                     .Where(entry => entry.State == EntityState.Unchanged && entry.Entity.VehicleKey == vehicleKey)
                     .ToArray())
        {
            stale.State = EntityState.Detached;
        }
        ManualChargingHoldRow? row = await dbContext.Set<ManualChargingHoldRow>()
            .SingleOrDefaultAsync(item => item.VehicleKey == vehicleKey, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }
        ManualChargingHoldRecordRow record = await dbContext.Set<ManualChargingHoldRecordRow>()
            .SingleAsync(item => item.HoldId == row.HoldId, cancellationToken).ConfigureAwait(false);
        dbContext.Set<ManualChargingHoldRow>().Remove(row);
        record.ReleasedAt = releasedAt;
        record.ReleaseRequestId = releaseRequestId;
        return true;
    }
}
