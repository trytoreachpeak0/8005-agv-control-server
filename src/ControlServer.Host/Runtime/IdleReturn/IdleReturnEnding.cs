using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.IdleReturn;

/// <summary>
/// 一次空闲返回的结束（批次8-19，control-server#390）：收敛与每一种失败收尾走同一个出口，暂存、不保存。
/// </summary>
/// <remarks>
/// <para>
/// <b>同一次保存里</b>：用途占有释放（它的记录写上 <paramref name="purposeReleaseReason"/>）、停靠标完成、旅程收尾（阶段 Completed、收尾码、
/// 撤下 <c>IDLE_RETURN</c> 的业务状态与跟着事实走的计划，<see cref="JourneyClosure.StageIdleReturnAsync"/>），以及给了的话等待点独占的释放。
/// 任一步没落库，其余都不落库。
/// </para>
/// <para>
/// <b>等待点独占在这里放不放，由调用方按离点证据定</b>：从没发出过单的承诺（车没动过）当场放；车可能动过、或停在点上的，不在这里放，
/// 由每轮开头的离点清扫凭离点证据放（<c>FixedStationExclusivitySweep</c>，与公共站点同一个判法）。收敛那一次是转占用，不放。
/// </para>
/// <para>
/// 两个调用方：引擎的空闲返回分支，与人工清除故障（<c>VehicleFaultRecoveryService</c>，FAILED 的单由人清除后按已确认失败收尾）。
/// </para>
/// </remarks>
internal static class IdleReturnEnding
{
    public static async Task StageAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        JourneyStopRow waitingPoint,
        string? reasonCode,
        string purposeReleaseReason,
        bool stillAtWaitingPoint,
        StationExclusivityRow? releaseStationNow,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(waitingPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(purposeReleaseReason);

        if (releaseStationNow is not null)
        {
            await WaitingPointExclusivity.StageReleaseAsync(
                    dbContext, releaseStationNow, now, reasonCode ?? purposeReleaseReason, cancellationToken)
                .ConfigureAwait(false);
        }
        waitingPoint.Status = JourneyStopStatuses.Completed;
        await JourneyPurposeClaimRelease.StageAsync(dbContext, runtime.JourneyId, now, purposeReleaseReason, cancellationToken)
            .ConfigureAwait(false);
        await JourneyClosure.StageIdleReturnAsync(
                dbContext, runtime, waitingPoint, reasonCode, stillAtWaitingPoint, now, cancellationToken)
            .ConfigureAwait(false);
    }
}
