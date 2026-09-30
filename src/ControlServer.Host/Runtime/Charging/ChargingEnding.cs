using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Charging;

/// <summary>
/// 一趟充电在到桩之前以已确认失败结束（批次9-06，control-server#404）：暂存、不保存，调用方与它自己的事实同一次保存。
/// </summary>
/// <remarks>
/// <para>
/// <b>同一次保存里</b>：充电周期结束（阶段 <c>ENDED</c>、结束时刻与原因）、<c>CHARGING</c> 用途占有释放（它的记录写上同一个原因）、停靠标完成、
/// 旅程收尾（阶段 Completed、收尾码、撤下 <c>CHARGING</c> 的业务状态与空计划，<see cref="JourneyClosure.StageChargingAsync"/>）。
/// 任一步没落库，其余都不落库。
/// </para>
/// <para>
/// <b>桩预占不在这里放</b>（<c>REQ-0173</c>）：预占只在充电已停、原车离开、桩位可确认空闲三项都确认之后释放。收尾这一刻车可能就停在桩上，
/// 所以由充电分配每轮开头的清扫凭那三项证据放（<c>ChargingAllocator</c>）。在那之前这辆车自己还占着桩，不会再被分配充电。
/// </para>
/// <para>
/// 两个调用方：引擎的充电分支（充电单被取消或删除、车已证明停稳），与人工清除故障（<c>VehicleFaultRecoveryService</c>，FAILED 的单由人清除后）。
/// 到桩之后的正常收尾（充满、离桩）是批次9-07 的，不经这里。
/// </para>
/// </remarks>
internal static class ChargingEnding
{
    public static async Task StageAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow runtime,
        JourneyStopRow charger,
        string reasonCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(charger);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);

        ChargingCycleRow? cycle = await dbContext.Set<ChargingCycleRow>()
            .SingleOrDefaultAsync(
                row => row.JourneyId == runtime.JourneyId && row.Phase != ChargingCyclePhases.Ended, cancellationToken)
            .ConfigureAwait(false);
        if (cycle is not null)
        {
            cycle.Phase = ChargingCyclePhases.Ended;
            cycle.EndedAt = now;
            cycle.EndReason = reasonCode;
            // The row's concurrency token: whoever else advanced this cycle since it was read makes this whole save fail.
            cycle.Version++;
        }

        charger.Status = JourneyStopStatuses.Completed;
        await JourneyPurposeClaimRelease.StageAsync(dbContext, runtime.JourneyId, now, reasonCode, cancellationToken)
            .ConfigureAwait(false);
        await JourneyClosure.StageChargingAsync(dbContext, runtime, reasonCode, now, cancellationToken).ConfigureAwait(false);
    }
}
