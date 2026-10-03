using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Charging;

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
}
