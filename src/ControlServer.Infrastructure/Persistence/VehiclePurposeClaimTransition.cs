using ControlServer.Application;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 一辆车的用途在同一趟旅程里换成另一种（批次9-08，control-server#406：充不上的车由 <c>CHARGING</c> 转为
/// <c>CLEARING_MAINTENANCE</c>，用户 2026-09-29 定「同一次保存」）。只暂存，调用方与它自己的事实同一次保存。
/// </summary>
/// <remarks>
/// <para>
/// <b>占有行就地改用途，不删了再插</b>：谁占着这辆车由 <c>VehiclePurposeClaims</c> 的主键决定（<see cref="VehiclePurposeClaimWrites"/>），
/// 在同一次保存里删掉再插同一个键，中间那一刻在别的上下文看来是「车空着」——那正是派车会抢进来的空档。改一列不放开主键，旅程也不变。
/// </para>
/// <para>
/// <b>经过照旧成对</b>：旧用途那一条开着的记录以 <paramref name="releaseReason"/> 关上，新用途另起一条开着的记录，与占有行同一次保存。
/// 所以「占有行说是 A、开着的记录说是 B」不会出现。
/// </para>
/// </remarks>
public static class VehiclePurposeClaimTransition
{
    /// <summary>
    /// 把 <paramref name="journeyId"/> 在 <paramref name="vehicleKey"/> 上的 <paramref name="from"/> 占有改成 <paramref name="to"/>。
    /// 占有不在、不是这趟旅程的、或用途不是 <paramref name="from"/> 时什么也不暂存，答假。
    /// </summary>
    public static async Task<bool> StageAsync(
        ControlServerDbContext dbContext,
        string vehicleKey,
        string journeyId,
        string from,
        string to,
        DateTimeOffset at,
        string releaseReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseReason);
        if (!VehiclePurposes.All.Contains(to, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(to), to, "Not one of the four vehicle purposes.");
        }

        VehiclePurposeClaimRow? claim = await dbContext.Set<VehiclePurposeClaimRow>()
            .SingleOrDefaultAsync(row => row.VehicleKey == vehicleKey && row.JourneyId == journeyId, cancellationToken)
            .ConfigureAwait(false);
        if (claim is null || !string.Equals(claim.Purpose, from, StringComparison.Ordinal))
        {
            return false;
        }

        claim.Purpose = to;
        claim.ClaimedAt = at;
        foreach (VehiclePurposeClaimRecordRow record in await dbContext.Set<VehiclePurposeClaimRecordRow>()
                     .Where(row => row.VehicleKey == vehicleKey && row.JourneyId == journeyId && row.ReleasedAt == null)
                     .ToListAsync(cancellationToken)
                     .ConfigureAwait(false))
        {
            record.ReleasedAt = at;
            record.ReleaseReason = releaseReason;
        }
        dbContext.Add(new VehiclePurposeClaimRecordRow
        {
            RecordId = Guid.NewGuid().ToString("D"),
            VehicleKey = vehicleKey,
            Purpose = to,
            JourneyId = journeyId,
            AcquiredAt = at
        });
        return true;
    }
}
