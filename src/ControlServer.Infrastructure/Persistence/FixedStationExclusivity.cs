using ControlServer.Application;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 固定公共站点单车位（<c>REQ-0204</c> 修订，批次8-20，control-server#391）：公共站点用与等待点同一个站点独占原语
/// （<see cref="StationExclusivityRow"/>，一站一行两状态），种类 <see cref="StationExclusivityKinds.FixedTaskStation"/>。
/// </summary>
/// <remarks>
/// <para>
/// <b>这里只暂存，不保存。</b>预占由承诺方的事务写（受理、追加），转占用由到站那一次推进写，都与各自那次保存同生灭——
/// 与让站（<see cref="StationYield"/>）同一个理由：受理成功而预占没写下，别的车下一轮就能被派来同一个站。
/// </para>
/// <para>
/// <b>谁占到由主键决定，不先读后写。</b>预占先读一次，只为分清「这辆车自己占着（上一趟留下的，在点等离点证据）」——那要
/// 把持有者交给新旅程，而不是插第二行。别的车占着、或者谁也没占着，都照样插入：别的车占着时主键拒绝整次保存，调用方把整笔承诺回滚；
/// 读到空而保存时已被别人抢先，同样由主键拒绝。
/// </para>
/// </remarks>
public static class FixedStationExclusivity
{
    /// <summary>公共站点独占的释放原因：离点证据满足（车已不在该站）。</summary>
    public const string ReleasedOnDepartureEvidence = "DEPARTED_STATION";

    /// <summary>公共站点独占的释放原因：同一辆车的新旅程接手了它（车仍在点或仍要去那里）。</summary>
    public const string HandedToNextJourney = "HANDED_TO_NEXT_JOURNEY";

    /// <summary>
    /// 暂存 <paramref name="journeyId"/> 对 <c>(mapId, stationId)</c> 的预占。这趟旅程已经持有时什么也不做；这辆车以别的旅程持有时
    /// 把持有者交给这趟旅程，状态不变（在点的仍是占用）；否则插入一行预占，由保存时的主键决定是否被别的车占着。
    /// </summary>
    public static async Task StageReserveAsync(
        ControlServerDbContext dbContext,
        int mapId,
        int stationId,
        string vehicleKey,
        string journeyId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);

        StationExclusivityRow? held = await TrackedAsync(dbContext, mapId, stationId, cancellationToken)
            .ConfigureAwait(false);
        if (held is not null)
        {
            // 同一个 (MapId, StationId) 不会既是等待点又是公共站点：批次8-17（control-server#388）的导入已拒绝。这里不再校验那条，
            // 只断言它——读到等待点的行就是那条校验被绕过了，接着写只会把一个等待点当成公共站点交出去。
            if (held.StationKind != StationExclusivityKinds.FixedTaskStation)
            {
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"Station {mapId}/{stationId} is held as {held.StationKind}, yet a demand names it as its fixed task station."));
            }
            if (held.JourneyId == journeyId)
            {
                return;
            }
            if (held.VehicleKey == vehicleKey)
            {
                await StageHandOverAsync(dbContext, held, journeyId, at, cancellationToken).ConfigureAwait(false);
                return;
            }
            // 别的车占着：照样插，让主键拒绝这次保存。不在这里抛，是为了读到的与保存时的由同一个仲裁者说了算。
            dbContext.Entry(held).State = EntityState.Detached;
        }

        dbContext.AddRange(StationExclusivityWrites.NewRows(
            new StationExclusivityRequest(
                mapId, stationId, StationExclusivityKinds.FixedTaskStation, StationExclusivityStates.Reserved, null),
            vehicleKey,
            journeyId,
            at));
    }

    /// <summary>
    /// 车到点了：这趟旅程在 <c>(mapId, stationId)</c> 上的预占转为占用，暂存。它没有在那里预占（没占着、已是占用、那不是公共站点）
    /// 时什么也不做。
    /// </summary>
    public static async Task StageOccupyOnArrivalAsync(
        ControlServerDbContext dbContext,
        int mapId,
        int stationId,
        string journeyId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        StationExclusivityRow? held = await TrackedAsync(dbContext, mapId, stationId, cancellationToken)
            .ConfigureAwait(false);
        if (held is not { StationKind: StationExclusivityKinds.FixedTaskStation, State: StationExclusivityStates.Reserved }
            || held.JourneyId != journeyId)
        {
            return;
        }

        StationExclusivityRecordRow record = await RecordOfAsync(dbContext, held, cancellationToken).ConfigureAwait(false);
        held.State = StationExclusivityStates.Occupied;
        held.StateSince = at;
        record.OccupiedAt = at;
    }

    /// <summary>暂存释放：删掉独占行，把时刻与原因写进它的经过。</summary>
    public static async Task StageReleaseAsync(
        ControlServerDbContext dbContext,
        StationExclusivityRow held,
        DateTimeOffset at,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(held);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        StationExclusivityRecordRow record = await RecordOfAsync(dbContext, held, cancellationToken).ConfigureAwait(false);
        dbContext.Set<StationExclusivityRow>().Remove(held);
        record.ReleasedAt = at;
        record.ReleaseReason = reason;
    }

    /// <summary>这次保存是不是被站点独占的主键拒绝的——也就是站点已被别的车占着。</summary>
    public static bool IsStationHeld(DbUpdateException failure) =>
        StationExclusivityWrites.IsKeyConflict(failure) && StationExclusivityWrites.IsStationConflict(failure);

    /// <summary>全部公共站点独占行，带跟踪（离点清扫要改它们）。</summary>
    public static async Task<IReadOnlyList<StationExclusivityRow>> ListTrackedAsync(
        ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        return await dbContext.Set<StationExclusivityRow>()
            .Where(row => row.StationKind == StationExclusivityKinds.FixedTaskStation)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    // The holder moves to the new journey in place: the key stays one row, the old passage is closed as handed over and a
    // new one opens with the state the row is in. Written against the holder read here -- JourneyId is a concurrency token,
    // so a release through another context in between refuses the save rather than resurrecting the row.
    private static async Task StageHandOverAsync(
        ControlServerDbContext dbContext,
        StationExclusivityRow held,
        string journeyId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        StationExclusivityRecordRow previous = await RecordOfAsync(dbContext, held, cancellationToken).ConfigureAwait(false);
        previous.ReleasedAt = at;
        previous.ReleaseReason = HandedToNextJourney;
        bool occupied = held.State == StationExclusivityStates.Occupied;
        string recordId = Guid.NewGuid().ToString("D");
        dbContext.Add(new StationExclusivityRecordRow
        {
            RecordId = recordId,
            MapId = held.MapId,
            StationId = held.StationId,
            StationKind = held.StationKind,
            VehicleKey = held.VehicleKey,
            JourneyId = journeyId,
            ReservedAt = occupied ? null : at,
            OccupiedAt = occupied ? at : null
        });
        held.JourneyId = journeyId;
        held.RecordId = recordId;
        held.StateSince = at;
    }

    private static async Task<StationExclusivityRow?> TrackedAsync(
        ControlServerDbContext dbContext, int mapId, int stationId, CancellationToken cancellationToken)
    {
        // A row this context saw earlier may since have been released through another one; forgetting it is not a read.
        StationExclusivityWrites.ForgetUnchangedStation(dbContext, mapId, stationId);
        return dbContext.Set<StationExclusivityRow>().Local
                   .SingleOrDefault(row => row.MapId == mapId && row.StationId == stationId &&
                                           dbContext.Entry(row).State != EntityState.Deleted)
               ?? await dbContext.Set<StationExclusivityRow>()
                   .SingleOrDefaultAsync(row => row.MapId == mapId && row.StationId == stationId, cancellationToken)
                   .ConfigureAwait(false);
    }

    private static async Task<StationExclusivityRecordRow> RecordOfAsync(
        ControlServerDbContext dbContext, StationExclusivityRow held, CancellationToken cancellationToken) =>
        dbContext.Set<StationExclusivityRecordRow>().Local.SingleOrDefault(record => record.RecordId == held.RecordId)
        ?? await dbContext.Set<StationExclusivityRecordRow>()
            .SingleAsync(record => record.RecordId == held.RecordId, cancellationToken).ConfigureAwait(false);
}
