using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 公共站点独占的离点释放（<c>REQ-0204</c> 修订，批次8-20，control-server#391）：车真的离开了公共站点，才放它的预占或占用。
/// </summary>
/// <remarks>
/// <para>
/// <b>离点证据，两条都要：</b>
/// </para>
/// <list type="number">
/// <item>持有的那趟旅程已不再以这个站为未完成的停靠——旅程已完成（或不在了），或那个停靠已完成（车离站了）、已移除。旅程还要去、
/// 或还站在那里，就还是它的。</item>
/// <item>RIoT 观测到车此刻在线、当前站是<b>另一个</b>站。当前站还是这个站、为空、读不到，都不算离开。</item>
/// </list>
/// <para>
/// <b>下达离站订单那一刻不放</b>：第 1 条在车为离站请求移动的那次保存里就成立了，第 2 条要等 RIoT 报出车已到了别的站。
/// 与等待点的 <c>REQ-0293</c>（「确认车辆实际离点后才释放，不能在下达离点订单时提前释放」）同一个判法；批次8-19（control-server#390）
/// 后合，复用它。代价是释放晚一些——车开出去、到了下一站才放——这只会让别的车多等，不会让两台车同时去一个站。
/// </para>
/// <para>
/// <b>旅程阻断时一律不放</b>，不论预占还是占用：阻断的旅程等的是人，它的订单结果可能未知、车可能还在动（与等待点「结果未知时保持
/// 车辆、目标点、用途与独占」同一条规则）。
/// </para>
/// <para>
/// <b>写绕开变更跟踪器。</b>上下文与整轮共用，这里一次跟踪保存会把推进留下的东西一起写掉；所以释放是一个事务里的两条直接更新，
/// 条件带着读到的持有者与经过，读完之后被别人改过的行不会被误删。
/// </para>
/// </remarks>
internal sealed class FixedStationDepartureRelease(
    ControlServerDbContext dbContext,
    IRiotVehicleFacts vehicleFacts,
    TimeProvider timeProvider,
    ILogger logger)
{
    private static readonly Action<ILogger, int, int, string, string, Exception?> LogReleased =
        LoggerMessage.Define<int, int, string, string>(
            LogLevel.Information,
            new EventId(2210, nameof(LogReleased)),
            "Fixed task station {MapId}/{StationId} released by vehicle {VehicleKey} on departure evidence (journey {JourneyId}).");

    private static readonly Action<ILogger, int, int, Exception?> LogReleaseFailed =
        LoggerMessage.Define<int, int>(
            LogLevel.Warning,
            new EventId(2211, nameof(LogReleaseFailed)),
            "Fixed task station {MapId}/{StationId} could not be judged for departure this round; it stays held and the next " +
            "round tries again.");

    public async Task ReleaseDepartedAsync(CancellationToken cancellationToken)
    {
        StationExclusivityRow[] held = await dbContext.Set<StationExclusivityRow>().AsNoTracking()
            .Where(row => row.StationKind == StationExclusivityKinds.FixedTaskStation)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (StationExclusivityRow row in held)
        {
            try
            {
                if (await HasDepartedAsync(row, cancellationToken).ConfigureAwait(false))
                {
                    await ReleaseAsync(row, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                LogReleaseFailed(logger, row.MapId, row.StationId, error);
            }
        }
    }

    /// <summary>离点证据两条是否都满足。</summary>
    internal async Task<bool> HasDepartedAsync(StationExclusivityRow row, CancellationToken cancellationToken)
    {
        JourneyRuntimeRow? journey = await dbContext.JourneyRuntimes.AsNoTracking()
            .SingleOrDefaultAsync(item => item.JourneyId == row.JourneyId, cancellationToken).ConfigureAwait(false);
        if (journey is { Stage: JourneyRuntimeStage.Blocked })
        {
            return false;
        }

        if (journey is not null && journey.Stage != JourneyRuntimeStage.Completed)
        {
            bool stillBound = await dbContext.Set<JourneyStopRow>().AsNoTracking()
                .AnyAsync(stop => stop.JourneyId == row.JourneyId &&
                                  stop.StationRiotId == row.StationId &&
                                  stop.Status != JourneyStopStatuses.Completed &&
                                  stop.Status != JourneyStopStatuses.Removed,
                    cancellationToken)
                .ConfigureAwait(false);
            if (stillBound)
            {
                return false;
            }
        }

        RiotVehicleObservation vehicle = await vehicleFacts.ReadVehicleAsync(row.VehicleKey, cancellationToken)
            .ConfigureAwait(false);
        return vehicle.Connected &&
               vehicle.CurrentStationId is int current &&
               current != row.StationId;
    }

    private async Task ReleaseAsync(StationExclusivityRow row, CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        int deleted = await dbContext.Set<StationExclusivityRow>()
            .Where(item => item.MapId == row.MapId && item.StationId == row.StationId &&
                           item.JourneyId == row.JourneyId && item.RecordId == row.RecordId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (deleted == 0)
        {
            // Released or handed over through another path since it was read: nothing of it is this sweep's any more.
            return;
        }

        await dbContext.Set<StationExclusivityRecordRow>()
            .Where(record => record.RecordId == row.RecordId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(record => record.ReleasedAt, now)
                    .SetProperty(record => record.ReleaseReason, FixedStationExclusivity.ReleasedOnDepartureEvidence),
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // A tracked copy would still read held; the next reserve reads again.
        foreach (var stale in dbContext.ChangeTracker.Entries<StationExclusivityRow>()
                     .Where(entry => entry.Entity.MapId == row.MapId && entry.Entity.StationId == row.StationId &&
                                     entry.State == EntityState.Unchanged)
                     .ToArray())
        {
            stale.State = EntityState.Detached;
        }
        LogReleased(logger, row.MapId, row.StationId, row.VehicleKey, row.JourneyId, null);
    }
}
