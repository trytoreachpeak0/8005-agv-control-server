using System.Collections.Concurrent;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 公共站点独占每轮开头的两步（<c>REQ-0204</c> 修订，批次8-20，control-server#391）：先凭离点证据释放，再给正开往公共站点而没有预占的车补预占。
/// </summary>
/// <remarks>
/// <para>
/// <b>离点证据，两条都要</b>（<see cref="HasDepartedAsync"/>）：持有的那趟旅程已不再以这个站为未完成的停靠——旅程已完成（或不在了），
/// 或那个停靠已完成（车离站了）、已移除；并且 RIoT 观测到车此刻在线、当前站是<b>另一个</b>站。当前站还是这个站、为空、读不到，都不算离开。
/// 下达离站订单那一刻第一条已经成立，第二条要等 RIoT 报出车到了别的站，所以<b>下达离站订单时不释放</b>——与等待点的 <c>REQ-0293</c>
/// 同一个判法，批次8-19（control-server#390）后合，复用它。
/// </para>
/// <para>
/// <b>旅程阻断时一律不放</b>，不论预占还是占用：阻断的旅程等的是人，它的订单结果可能未知、车可能还在动（与等待点「结果未知时保持
/// 车辆、目标点、用途与独占」同一条规则）。
/// </para>
/// <para>
/// <b>补预占（调度 2026-09-29 定）。</b>一辆车的下一站变成公共站点，不只发生在受理与追加（那两处在承诺的同一次保存里预占），也发生在推进里：
/// <c>WIRE_TO_GATE</c> 的车站到最后一个取货停靠上时，关卡就成了它的下一站（<c>StationYield.NextStop</c> 的定义）。那一刻站可能被别的车占着。
/// 它<b>照常出发、不在机台等</b>——带货停在机台会连带占住机台，把一处等待扩成两处，也改变今天现场由 RIoT 交通管理在关卡前排队的行为；
/// 所以引擎的离站段一行不改。取而代之的是这里：每轮开头、派车之前，给这类车补一次预占，站一放就先给它，排在任何新任务之前；
/// 取不到就记一条 <see cref="HeldOnApproachReason"/>（每趟旅程每个站一次）。在它取得之前，派车判据
/// （<c>FixedStationSingleOccupancyCriterion</c>）把它当作这个站的持有者，别的新任务不能预占它正开往的站——独占表因此不会让第三辆车插进来。
/// </para>
/// <para>
/// <b>释放的写绕开变更跟踪器</b>：上下文与整轮共用，一次跟踪保存会把别处留下的东西一起写掉，所以释放是一个事务里的两条直接更新，条件带着读到的
/// 持有者与经过。<b>补预占走跟踪保存</b>（交接要改那一行），所以先确认跟踪器里没有别的待存改动，有就这一轮不补。
/// </para>
/// </remarks>
internal sealed class FixedStationExclusivitySweep(
    ControlServerDbContext dbContext,
    IRiotVehicleFacts vehicleFacts,
    TimeProvider timeProvider,
    ILogger logger,
    FixedStationSweepWarnings warnings)
{
    /// <summary>车正开往一个被别的车预占或占用着的公共站点、自己没取得预占时记的原因码。</summary>
    public const string HeldOnApproachReason = "FIXED_TASK_STATION_HELD_ON_APPROACH";


    private static readonly Action<ILogger, int, int, string, string, Exception?> LogReleased =
        LoggerMessage.Define<int, int, string, string>(
            LogLevel.Information,
            new EventId(2210, nameof(LogReleased)),
            "Fixed task station {MapId}/{StationId} released by vehicle {VehicleKey} on departure evidence (journey {JourneyId}).");

    private static readonly Action<ILogger, int, int, Exception?> LogSweepFailed =
        LoggerMessage.Define<int, int>(
            LogLevel.Warning,
            new EventId(2211, nameof(LogSweepFailed)),
            "Fixed task station {MapId}/{StationId} could not be released or reserved this round; it stays as it is and the " +
            "next round tries again.");

    private static readonly Action<ILogger, string, string, int, int, string, string, Exception?> LogHeldOnApproach =
        LoggerMessage.Define<string, string, int, int, string, string>(
            LogLevel.Warning,
            new EventId(2212, nameof(LogHeldOnApproach)),
            "{ReasonCode}: vehicle {VehicleKey} is heading for fixed task station {MapId}/{StationId} held by vehicle " +
            "{HolderVehicleKey} (journey {JourneyId}); it goes on and is given the station once it is released.");

    private static readonly Action<ILogger, Exception?> LogPendingChanges =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(2213, nameof(LogPendingChanges)),
            "The fixed task station reservation sweep found unsaved changes in the round's context and skipped this round, " +
            "so as not to save them with its own.");

    private static readonly Action<ILogger, string, Exception?> LogRecovered =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(2214, nameof(LogRecovered)),
            "The fixed task station sweep's warning {WarningKey} has cleared.");

    /// <summary>凭离点证据释放。</summary>
    public async Task ReleaseDepartedAsync(CancellationToken cancellationToken)
    {
        StationExclusivityRow[] held = await dbContext.Set<StationExclusivityRow>().AsNoTracking()
            .Where(row => row.StationKind == StationExclusivityKinds.FixedTaskStation)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (StationExclusivityRow row in held)
        {
            string warning = $"release|{row.MapId}/{row.StationId}";
            try
            {
                if (await HasDepartedAsync(row, cancellationToken).ConfigureAwait(false))
                {
                    await ReleaseAsync(row, cancellationToken).ConfigureAwait(false);
                }
                Clear(warning);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                if (Raise(warning, error.GetType().FullName!))
                {
                    LogSweepFailed(logger, row.MapId, row.StationId, error);
                }
            }
        }

        HashSet<string> stillHeld = [.. held.Select(row => $"release|{row.MapId}/{row.StationId}")];
        Forget(key => key.StartsWith("release|", StringComparison.Ordinal) && !stillHeld.Contains(key));
    }

    /// <summary>
    /// 给下一站是 <paramref name="publicStations"/> 之一、却没有它的预占的在途车补预占，最早受理的先给。
    /// </summary>
    public async Task ReserveApproachingAsync(IReadOnlySet<int> publicStations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publicStations);
        if (publicStations.Count == 0)
        {
            return;
        }
        if (dbContext.ChangeTracker.HasChanges())
        {
            if (Raise("pending", "pending"))
            {
                LogPendingChanges(logger, null);
            }
            return;
        }
        Clear("pending");

        IReadOnlyList<(JourneyRuntimeRow Journey, JourneyStopRow Next)> approaching = await ApproachingAsync(
            dbContext, publicStations, excludeVehicleKey: null, cancellationToken).ConfigureAwait(false);
        foreach ((JourneyRuntimeRow journey, JourneyStopRow next) in approaching)
        {
            string approach = $"{journey.JourneyId}|{next.StationRiotId}";
            try
            {
                StationExclusivityRow? held = await dbContext.Set<StationExclusivityRow>().AsNoTracking()
                    .SingleOrDefaultAsync(
                        row => row.MapId == journey.MapId && row.StationId == next.StationRiotId, cancellationToken)
                    .ConfigureAwait(false);
                if (held?.JourneyId == journey.JourneyId)
                {
                    warnings.Open.TryRemove($"held|{approach}", out _);
                    Clear($"reserve|{approach}");
                    continue;
                }
                if (held is not null && held.VehicleKey != journey.VehicleKey)
                {
                    Clear($"reserve|{approach}");
                    if (Raise($"held|{approach}", held.VehicleKey))
                    {
                        LogHeldOnApproach(logger, HeldOnApproachReason, journey.VehicleKey, journey.MapId,
                            next.StationRiotId, held.VehicleKey, journey.JourneyId, null);
                    }
                    continue;
                }

                await FixedStationExclusivity.StageReserveAsync(
                        dbContext, journey.MapId, next.StationRiotId, journey.VehicleKey, journey.JourneyId,
                        timeProvider.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                warnings.Open.TryRemove($"held|{approach}", out _);
                Clear($"reserve|{approach}");
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                // Taken between the read and the insert (the key refused it), or the save failed: nothing of it was written.
                // Forget what was staged, so neither this round's later saves nor the next journey's carry it.
                ForgetStagedExclusivity();
                if ((error is not DbUpdateException failure || !FixedStationExclusivity.IsStationHeld(failure)) &&
                    Raise($"reserve|{approach}", error.GetType().FullName!))
                {
                    LogSweepFailed(logger, journey.MapId, next.StationRiotId, error);
                }
            }
        }

        // A journey that finished, was blocked or turned away without ever getting its station leaves nothing behind.
        HashSet<string> stillApproaching =
            [.. approaching.Select(item => $"{item.Journey.JourneyId}|{item.Next.StationRiotId}")];
        Forget(key => (key.StartsWith("held|", StringComparison.Ordinal) || key.StartsWith("reserve|", StringComparison.Ordinal)) &&
                      !stillApproaching.Contains(key[(key.IndexOf('|') + 1)..]));
    }

    /// <summary>打开（或换了原因）时为真，调用方据此只打一次。</summary>
    private bool Raise(string key, string reason)
    {
        bool fresh = true;
        warnings.Open.AddOrUpdate(key, reason, (_, open) =>
        {
            fresh = !string.Equals(open, reason, StringComparison.Ordinal);
            return reason;
        });
        return fresh;
    }

    /// <summary>它开着就关上，并打一条恢复。</summary>
    private void Clear(string key)
    {
        if (warnings.Open.TryRemove(key, out _))
        {
            LogRecovered(logger, key, null);
        }
    }

    private void Forget(Func<string, bool> stale)
    {
        foreach (string key in warnings.Open.Keys.Where(stale).ToArray())
        {
            warnings.Open.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// 下一站（<c>StationYield.NextStop</c>）是 <paramref name="publicStations"/> 之一的在途旅程，按受理先后。不读跟踪实例：
    /// 派车判据与这里读的都是库里此刻的样子。
    /// </summary>
    internal static async Task<IReadOnlyList<(JourneyRuntimeRow Journey, JourneyStopRow Next)>> ApproachingAsync(
        ControlServerDbContext dbContext,
        IReadOnlySet<int> publicStations,
        string? excludeVehicleKey,
        CancellationToken cancellationToken)
    {
        JourneyRuntimeRow[] journeys = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.Stage != JourneyRuntimeStage.Completed && row.Stage != JourneyRuntimeStage.Blocked &&
                          row.VehicleKey != excludeVehicleKey)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        List<(JourneyRuntimeRow, JourneyStopRow)> approaching = [];
        foreach (JourneyRuntimeRow journey in journeys.OrderBy(row => row.CreatedAt))
        {
            JourneyStopRow[] stops = await dbContext.Set<JourneyStopRow>().AsNoTracking()
                .Where(stop => stop.JourneyId == journey.JourneyId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            if (StationYield.NextStop(journey.Stage, stops) is { } next && publicStations.Contains(next.StationRiotId))
            {
                approaching.Add((journey, next));
            }
        }
        return approaching;
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

    private void ForgetStagedExclusivity()
    {
        foreach (var entry in dbContext.ChangeTracker.Entries()
                     .Where(entry => entry.Entity is StationExclusivityRow or StationExclusivityRecordRow)
                     .ToArray())
        {
            entry.State = EntityState.Detached;
        }
    }
}

/// <summary>
/// 公共站点独占清扫开着的告警（#418 审查）：同一件事、同一个原因只在打开与恢复时各记一次，而不是每轮都记。
/// </summary>
/// <remarks>
/// 键是告警说的那件事——<c>release|map/station</c>、<c>reserve|journey|station</c>、<c>held|journey|station</c>、<c>pending</c>——
/// 值是打开时的原因。每轮末尾丢掉已不在途的旅程与已不被占的站的键，所以没拿到站就完成的旅程什么也不留下。
/// 清扫每轮新建（引擎是每轮一个作用域），所以这份状态由主机注册成单例交给引擎；引擎没拿到时自带一份。
/// </remarks>
public sealed class FixedStationSweepWarnings
{
    internal ConcurrentDictionary<string, string> Open { get; } = new(StringComparer.Ordinal);

    /// <summary>此刻还开着的告警键。</summary>
    public IReadOnlyCollection<string> OpenKeys => [.. Open.Keys];
}
