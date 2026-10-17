using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Charging;

/// <summary>
/// 一辆车此刻与 v2 充电周期的关系（control-server#744）：派车电量判据里「报 <c>CHARGING</c> 的车能不能接活」按它分三支。
/// </summary>
public enum ChargingCycleStanding
{
    /// <summary>没有未结束的周期：车若报 <c>CHARGING</c>，是在 v2 周期之外被充上的（例如低电断电后人工推上桩）。</summary>
    None,

    /// <summary>有未结束的周期，还没充满（含去桩途中、充电中、充不上之后的清桩）。</summary>
    InProgress,

    /// <summary>本周期已 <c>COMPLETE</c>、车还在桩上（周期未结束）。</summary>
    CompleteOnCharger,
}

/// <summary>
/// 派车、空闲返回读「这辆车本周期已充满、还在桩上」的那一个读法（批次9-07，control-server#405）。
/// </summary>
/// <remarks>
/// 充满（<c>COMPLETE</c>）之后周期不结束：用途放了，桩仍是这辆车的占用，直到它取得下一用途离桩、三项确认之后清扫才收尾。所以「未结束、线上状态
/// <c>COMPLETE</c>」就是「充满了、还没离桩」。只放开报 <c>CHARGING</c> 那一支；充满之前的周期答否。
/// </remarks>
internal static class ChargingCycleFacts
{
    public static Task<bool> CompleteOnChargerAsync(
        ControlServerDbContext dbContext,
        string vehicleKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        return dbContext.Set<ChargingCycleRow>().AsNoTracking()
            .AnyAsync(
                row => row.VehicleKey == vehicleKey && row.Phase == ChargingCyclePhases.Active &&
                       row.WireState == ChargingCycleWireStates.Complete,
                cancellationToken);
    }

    /// <summary>
    /// 这辆车此刻与 v2 充电周期的关系（control-server#744）：本周期已充满在桩上、有未结束而没充满的周期、或一个未结束的周期都没有。
    /// </summary>
    public static async Task<ChargingCycleStanding> StandingAsync(
        ControlServerDbContext dbContext,
        string vehicleKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        (string Phase, string WireState)[] open =
        [
            .. (await dbContext.Set<ChargingCycleRow>().AsNoTracking()
                    .Where(row => row.VehicleKey == vehicleKey && row.Phase != ChargingCyclePhases.Ended)
                    .Select(row => new { row.Phase, row.WireState })
                    .ToArrayAsync(cancellationToken)
                    .ConfigureAwait(false))
                .Select(row => (row.Phase, row.WireState)),
        ];
        return open.Length == 0
            ? ChargingCycleStanding.None
            : open.Any(row => row.Phase == ChargingCyclePhases.Active && row.WireState == ChargingCycleWireStates.Complete)
                ? ChargingCycleStanding.CompleteOnCharger
                : ChargingCycleStanding.InProgress;
    }
}
