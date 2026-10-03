using ControlServer.Application;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 空闲返回在等待点上的独占（<c>REQ-0293</c>；批次8-19，control-server#390）：到点那一次保存里预占转占用，与释放用途占有同一次保存。
/// </summary>
/// <remarks>
/// <para>
/// <b>只暂存，不保存</b>，理由同 <see cref="FixedStationExclusivity"/>：转占用与释放用途、旅程收尾要么都在、要么都不在——
/// 中间崩溃不许留下「用途已放、独占仍是预占」。
/// </para>
/// <para>
/// 离点释放不在这里：它由每轮开头的离点清扫凭离点证据做（<c>FixedStationExclusivitySweep</c>，与公共站点同一个判法）。
/// </para>
/// </remarks>
public static class WaitingPointExclusivity
{
    /// <summary>
    /// 这趟旅程在 <c>(mapId, stationId)</c> 上的等待点独占，带跟踪；这一行不在、或不是这趟旅程的（被人工释放、之后被别的车取得，
    /// control-server#419）时为空。
    /// </summary>
    public static async Task<StationExclusivityRow?> HeldByAsync(
        ControlServerDbContext dbContext,
        int mapId,
        int stationId,
        string journeyId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        // A row this context saw earlier may since have been released through another one; forgetting it is not a read.
        StationExclusivityWrites.ForgetUnchangedStation(dbContext, mapId, stationId);
        StationExclusivityRow? row = await dbContext.Set<StationExclusivityRow>()
            .SingleOrDefaultAsync(item => item.MapId == mapId && item.StationId == stationId, cancellationToken)
            .ConfigureAwait(false);
        return row is { StationKind: StationExclusivityKinds.WaitingPoint } && row.JourneyId == journeyId ? row : null;
    }

    /// <summary>
    /// 车到点了：<paramref name="held"/>（这趟旅程的等待点预占）转为占用，暂存。已是占用时什么也不改（崩溃后重做）。
    /// </summary>
    public static async Task StageOccupyAsync(
        ControlServerDbContext dbContext,
        StationExclusivityRow held,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(held);
        if (held.State == StationExclusivityStates.Occupied)
        {
            return;
        }

        StationExclusivityRecordRow record = dbContext.Set<StationExclusivityRecordRow>().Local
                                                 .SingleOrDefault(item => item.RecordId == held.RecordId)
                                             ?? await dbContext.Set<StationExclusivityRecordRow>()
                                                 .SingleAsync(item => item.RecordId == held.RecordId, cancellationToken)
                                                 .ConfigureAwait(false);
        held.State = StationExclusivityStates.Occupied;
        held.StateSince = at;
        record.OccupiedAt = at;
    }

    /// <summary>暂存释放：删掉这一行，把时刻与原因写进它的经过。给从没出发过的承诺用（物化不出旅程、建单前重新核验失败）。</summary>
    public static Task StageReleaseAsync(
        ControlServerDbContext dbContext,
        StationExclusivityRow held,
        DateTimeOffset at,
        string reason,
        CancellationToken cancellationToken) =>
        FixedStationExclusivity.StageReleaseAsync(dbContext, held, at, reason, cancellationToken);
}
