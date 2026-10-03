using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.Infrastructure.Persistence;

// 批次 8 建表票 control-server#386 的三个存储。用途占有的写入与释放经 VehiclePurposeClaimWrites，引擎（批次8-16，control-server#387）也走它。

/// <summary>
/// 用途占有：认领时占有行、占有记录（以及请求了的站点独占与它的经过）在同一次保存里插入，谁占到由约束决定，不先读后写。
/// </summary>
public sealed class VehiclePurposeLedgerStore(ControlServerDbContext context) : IVehiclePurposeLedger
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<VehiclePurposeClaim?> ReadClaimAsync(string vehicleKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        VehiclePurposeClaimRow? row = await _context.Set<VehiclePurposeClaimRow>().AsNoTracking()
            .SingleOrDefaultAsync(claim => claim.VehicleKey == vehicleKey, cancellationToken);
        return row is null ? null : new VehiclePurposeClaim(row.VehicleKey, row.Purpose, row.JourneyId, row.ClaimedAt);
    }

    public async Task<VehiclePurposeAcquisitionOutcome> TryAcquireAsync(
        VehiclePurposeClaim claim,
        StationExclusivityRequest? station,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentException.ThrowIfNullOrWhiteSpace(claim.VehicleKey, nameof(claim));
        ArgumentException.ThrowIfNullOrWhiteSpace(claim.Purpose, nameof(claim));
        ArgumentException.ThrowIfNullOrWhiteSpace(claim.JourneyId, nameof(claim));
        if (station is not null)
        {
            StationExclusivityWrites.Validate(station);
        }

        // The purpose itself is not checked here: the CHECK constraint is what refuses a fifth one.
        StationExclusivityWrites.ForgetUnchangedClaims(_context, claim.VehicleKey);
        List<object> staged = [.. VehiclePurposeClaimWrites.NewRows(claim)];
        if (station is not null)
        {
            StationExclusivityWrites.ForgetUnchangedStation(_context, station.MapId, station.StationId);
            staged.AddRange(StationExclusivityWrites.NewRows(station, claim.VehicleKey, claim.JourneyId, claim.ClaimedAt));
        }
        _context.AddRange(staged);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return VehiclePurposeAcquisitionOutcome.Acquired;
        }
        catch (DbUpdateException failure) when (StationExclusivityWrites.IsKeyConflict(failure))
        {
            // One of the keys refused the save, and the save took every row of this acquisition back with it. Which key
            // refused says little -- EF inserts the station before the claim, so a retry of an acquisition that already
            // succeeded is refused by the station's key -- so the answer is read from who holds what now.
            foreach (object row in staged)
            {
                _context.Entry(row).State = EntityState.Detached;
            }
            VehiclePurposeClaim? vehicleHolder = await ReadClaimAsync(claim.VehicleKey, cancellationToken);
            StationExclusivityRow? stationHolder = station is null
                ? null
                : await _context.Set<StationExclusivityRow>().AsNoTracking()
                    .SingleOrDefaultAsync(
                        row => row.MapId == station.MapId && row.StationId == station.StationId, cancellationToken);
            bool vehicleIsOurs = vehicleHolder is not null
                                 && vehicleHolder.JourneyId == claim.JourneyId && vehicleHolder.Purpose == claim.Purpose;
            bool stationIsOurs = station is null
                                 || (stationHolder is not null && stationHolder.JourneyId == claim.JourneyId
                                     && stationHolder.VehicleKey == claim.VehicleKey);
            if (vehicleIsOurs && stationIsOurs)
            {
                return VehiclePurposeAcquisitionOutcome.AlreadyHeld;
            }
            if (vehicleHolder is not null)
            {
                return VehiclePurposeAcquisitionOutcome.VehicleHeld;
            }
            if (station is not null && stationIsOurs)
            {
                // The vehicle is free but the station is already this journey's: its claim was released and the station
                // not yet (the station waits for departure evidence, REQ-0293). Not "held by someone else".
                return VehiclePurposeAcquisitionOutcome.StationAlreadyHeld;
            }
            if (stationHolder is not null)
            {
                return VehiclePurposeAcquisitionOutcome.StationHeld;
            }
            // Neither is held now: whoever refused this one let go between the refusal and these reads, and the caller's
            // next round decides again.
            return StationExclusivityWrites.IsStationConflict(failure)
                ? VehiclePurposeAcquisitionOutcome.StationHeld
                : VehiclePurposeAcquisitionOutcome.VehicleHeld;
        }
    }

    public async Task<bool> ReleaseAsync(
        string vehicleKey,
        string journeyId,
        DateTimeOffset releasedAt,
        string releaseReason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseReason);

        if (!await VehiclePurposeClaimWrites.StageReleaseAsync(
                _context, vehicleKey, journeyId, releasedAt, releaseReason, cancellationToken))
        {
            return false;
        }
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Released through another context between the read and this delete.
            _context.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<IReadOnlyList<VehiclePurposeClaimRecord>> ListClaimHistoryAsync(
        string vehicleKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        VehiclePurposeClaimRecordRow[] rows = await _context.Set<VehiclePurposeClaimRecordRow>().AsNoTracking()
            .Where(row => row.VehicleKey == vehicleKey)
            .ToArrayAsync(cancellationToken);
        // Ordered in memory: SQLite cannot order by a DateTimeOffset column.
        return
        [
            .. rows.OrderBy(row => row.AcquiredAt).ThenBy(row => row.RecordId, StringComparer.Ordinal)
                .Select(row => new VehiclePurposeClaimRecord(
                    row.RecordId, row.VehicleKey, row.Purpose, row.JourneyId, row.AcquiredAt, row.ReleasedAt,
                    row.ReleaseReason))
        ];
    }
}

/// <summary>
/// 站点独占。取得时独占行与经过行同一次保存，谁占到由主键 <c>(MapId, StationId)</c> 决定；转占用与释放只对读到时的持有者与状态生效。
/// </summary>
public sealed class StationExclusivityStore(ControlServerDbContext context) : IStationExclusivityStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<StationExclusivityAcquisitionOutcome> TryAcquireAsync(
        StationExclusivityRequest request,
        string vehicleKey,
        string journeyId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        StationExclusivityWrites.Validate(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);

        StationExclusivityWrites.ForgetUnchangedStation(_context, request.MapId, request.StationId);
        object[] staged = StationExclusivityWrites.NewRows(request, vehicleKey, journeyId, at);
        _context.AddRange(staged);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return StationExclusivityAcquisitionOutcome.Acquired;
        }
        catch (DbUpdateException failure) when (StationExclusivityWrites.IsKeyConflict(failure))
        {
            foreach (object row in staged)
            {
                _context.Entry(row).State = EntityState.Detached;
            }
            // The key refused the insert; only who holds the station now decides the answer.
            StationExclusivity? holder = await ReadAsync(request.MapId, request.StationId, cancellationToken);
            return holder is not null && holder.JourneyId == journeyId && holder.VehicleKey == vehicleKey
                ? StationExclusivityAcquisitionOutcome.AlreadyHeld
                : StationExclusivityAcquisitionOutcome.Held;
        }
    }

    public async Task<bool> MarkOccupiedAsync(
        int mapId, int stationId, string journeyId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        StationExclusivityRow? row = await HeldByAsync(mapId, stationId, journeyId, cancellationToken);
        if (row is null)
        {
            return false;
        }
        if (row.State == StationExclusivityStates.Occupied)
        {
            return true;
        }
        StationExclusivityRecordRow record = await RecordOfAsync(row, cancellationToken);
        row.State = StationExclusivityStates.Occupied;
        row.StateSince = at;
        record.OccupiedAt = at;
        return await SaveUnlessMovedAsync(cancellationToken);
    }

    public async Task<bool> ReleaseAsync(
        int mapId,
        int stationId,
        string journeyId,
        DateTimeOffset releasedAt,
        string releaseReason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseReason);
        StationExclusivityRow? row = await HeldByAsync(mapId, stationId, journeyId, cancellationToken);
        if (row is null)
        {
            return false;
        }
        StationExclusivityRecordRow record = await RecordOfAsync(row, cancellationToken);
        _context.Set<StationExclusivityRow>().Remove(row);
        record.ReleasedAt = releasedAt;
        record.ReleaseReason = releaseReason;
        return await SaveUnlessMovedAsync(cancellationToken);
    }

    public async Task<StationExclusivity?> ReadAsync(int mapId, int stationId, CancellationToken cancellationToken)
    {
        StationExclusivityRow? row = await _context.Set<StationExclusivityRow>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.MapId == mapId && item.StationId == stationId, cancellationToken);
        return row is null ? null : ToModel(row);
    }

    public async Task<IReadOnlyList<StationExclusivity>> ListByVehicleAsync(
        string vehicleKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        StationExclusivityRow[] rows = await _context.Set<StationExclusivityRow>().AsNoTracking()
            .Where(row => row.VehicleKey == vehicleKey)
            .OrderBy(row => row.MapId).ThenBy(row => row.StationId)
            .ToArrayAsync(cancellationToken);
        return [.. rows.Select(ToModel)];
    }

    public async Task<IReadOnlyList<StationExclusivityRecord>> ListHistoryAsync(
        int mapId, int stationId, CancellationToken cancellationToken)
    {
        StationExclusivityRecordRow[] rows = await _context.Set<StationExclusivityRecordRow>().AsNoTracking()
            .Where(row => row.MapId == mapId && row.StationId == stationId)
            .ToArrayAsync(cancellationToken);
        // Ordered in memory: SQLite cannot order by a DateTimeOffset column.
        return
        [
            .. rows.OrderBy(row => row.ReservedAt ?? row.OccupiedAt).ThenBy(row => row.RecordId, StringComparer.Ordinal)
                .Select(row => new StationExclusivityRecord(
                    row.RecordId, row.MapId, row.StationId, row.StationKind, row.VehicleKey, row.JourneyId,
                    row.WaitingPointVersion, row.ReservedAt, row.OccupiedAt, row.ReleasedAt, row.ReleaseReason,
                    row.ChargerRosterVersion))
        ];
    }

    private async Task<StationExclusivityRow?> HeldByAsync(
        int mapId, int stationId, string journeyId, CancellationToken cancellationToken)
    {
        StationExclusivityWrites.ForgetUnchangedStation(_context, mapId, stationId);
        return await _context.Set<StationExclusivityRow>()
            .SingleOrDefaultAsync(
                row => row.MapId == mapId && row.StationId == stationId && row.JourneyId == journeyId,
                cancellationToken);
    }

    private Task<StationExclusivityRecordRow> RecordOfAsync(StationExclusivityRow row, CancellationToken cancellationToken) =>
        _context.Set<StationExclusivityRecordRow>().SingleAsync(record => record.RecordId == row.RecordId, cancellationToken);

    private async Task<bool> SaveUnlessMovedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // The holder or the state changed through another context after this one read the row: nothing of this
            // change was written.
            _context.ChangeTracker.Clear();
            return false;
        }
    }

    private static StationExclusivity ToModel(StationExclusivityRow row) =>
        new(row.MapId, row.StationId, row.StationKind, row.State, row.VehicleKey, row.JourneyId, row.StateSince,
            row.WaitingPointVersion, row.ChargerRosterVersion);
}

/// <summary>The rows and the conflict classification the ledger and the station store share.</summary>
internal static class StationExclusivityWrites
{
    // SQLITE_CONSTRAINT_PRIMARYKEY and SQLITE_CONSTRAINT_UNIQUE. A CHECK (275) or NOT NULL (1299) failure is also error
    // 19, and is deliberately not a conflict: it is a bad request, and it propagates.
    private const int PrimaryKeyConstraint = 1555;
    private const int UniqueConstraint = 2067;

    internal static void Validate(StationExclusivityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StationKind, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.State, nameof(request));
    }

    internal static object[] NewRows(
        StationExclusivityRequest request, string vehicleKey, string journeyId, DateTimeOffset at)
    {
        string recordId = Guid.NewGuid().ToString("D");
        bool occupied = request.State == StationExclusivityStates.Occupied;
        return
        [
            new StationExclusivityRow
            {
                MapId = request.MapId,
                StationId = request.StationId,
                StationKind = request.StationKind,
                State = request.State,
                VehicleKey = vehicleKey,
                JourneyId = journeyId,
                StateSince = at,
                WaitingPointVersion = request.WaitingPointVersion,
                RecordId = recordId,
                ChargerRosterVersion = request.ChargerRosterVersion
            },
            new StationExclusivityRecordRow
            {
                RecordId = recordId,
                MapId = request.MapId,
                StationId = request.StationId,
                StationKind = request.StationKind,
                VehicleKey = vehicleKey,
                JourneyId = journeyId,
                WaitingPointVersion = request.WaitingPointVersion,
                ReservedAt = occupied ? null : at,
                OccupiedAt = occupied ? at : null,
                ChargerRosterVersion = request.ChargerRosterVersion
            }
        ];
    }

    internal static bool IsKeyConflict(DbUpdateException failure) =>
        failure.InnerException is SqliteException { SqliteExtendedErrorCode: PrimaryKeyConstraint or UniqueConstraint };

    internal static bool IsStationConflict(DbUpdateException failure) =>
        failure.InnerException is SqliteException sqlite &&
        sqlite.Message.Contains("StationExclusivit", StringComparison.Ordinal);

    // See WireToGateStore.ForgetClaimsThisContextLastSaw: a row this context saw earlier may since have been released
    // through another context, and a second tracked instance with its key is refused in memory before the database is
    // asked. Forgetting it is not a read.
    internal static void ForgetUnchangedClaims(ControlServerDbContext context, string vehicleKey)
    {
        foreach (var stale in context.ChangeTracker.Entries<VehiclePurposeClaimRow>()
                     .Where(entry => entry.State == EntityState.Unchanged && entry.Entity.VehicleKey == vehicleKey)
                     .ToArray())
        {
            stale.State = EntityState.Detached;
        }
    }

    internal static void ForgetUnchangedStation(ControlServerDbContext context, int mapId, int stationId)
    {
        foreach (var stale in context.ChangeTracker.Entries<StationExclusivityRow>()
                     .Where(entry => entry.State == EntityState.Unchanged
                                     && entry.Entity.MapId == mapId && entry.Entity.StationId == stationId)
                     .ToArray())
        {
            stale.State = EntityState.Detached;
        }
    }
}

/// <summary>
/// 等待点登记。版本行、等待点行、白名单行与治理快照、业务审计在同一个事务里；内容与当前版本相同时不写。
/// </summary>
public sealed class WaitingPointRegistry(
    ControlServerDbContext context,
    GovernedConfigurationPublisher publisher) : IWaitingPointRegistry
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly GovernedConfigurationPublisher _publisher =
        publisher ?? throw new ArgumentNullException(nameof(publisher));

    public async Task<WaitingPointRegistrationVersion> WriteVersionAsync(
        IReadOnlyList<WaitingPointEntry> points, DateTimeOffset loadedAt, CancellationToken cancellationToken)
    {
        WaitingPointEntry[] ordered = Normalise(points);
        string contentJson = ContentJson(ordered);

        await using IDbContextTransaction? transaction = _context.Database.CurrentTransaction is null
            ? await _context.Database.BeginTransactionAsync(cancellationToken)
            : null;

        WaitingPointRegistrationVersion? current = await ReadCurrentAsync(cancellationToken);
        if (current is not null && current.ContentSha256 == Sha256(contentJson))
        {
            return current;
        }

        long version = (current?.Version ?? 0) + 1;
        GovernedConfigurationSnapshot snapshot = await _publisher.PublishVersionAsync(
            GovernedObjectKind.WaitingPointRegistration,
            WaitingPointGovernance.ObjectId,
            version,
            contentJson,
            WaitingPointGovernance.VersionImportedAction,
            loadedAt,
            cancellationToken);

        _context.Set<WaitingPointVersionRow>().Add(new WaitingPointVersionRow
        {
            Version = version,
            ContentSha256 = snapshot.ContentSha256,
            SnapshotId = snapshot.SnapshotId,
            LoadedAt = loadedAt,
            Source = WaitingPointSources.GovernedImport
        });
        _context.Set<WaitingPointRow>().AddRange(ordered.Select(point => new WaitingPointRow
        {
            Version = version,
            MapId = point.MapId,
            StationId = point.StationId,
            StationName = point.StationName,
            Enabled = point.Enabled
        }));
        _context.Set<WaitingPointVehicleScopeRow>().AddRange(ordered.SelectMany(point => point.VehicleScope.Select(
            vehicleKey => new WaitingPointVehicleScopeRow
            {
                Version = version,
                MapId = point.MapId,
                StationId = point.StationId,
                VehicleKey = vehicleKey
            })));
        await _context.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new WaitingPointRegistrationVersion(
            version, snapshot.ContentSha256, snapshot.SnapshotId, loadedAt, WaitingPointSources.GovernedImport, ordered);
    }

    public Task<WaitingPointRegistrationVersion?> ReadCurrentAsync(CancellationToken cancellationToken) =>
        ReadCurrentFromAsync(_context, cancellationToken);

    public Task<WaitingPointRegistrationVersion?> ReadVersionAsync(long version, CancellationToken cancellationToken) =>
        ReadVersionFromAsync(_context, version, cancellationToken);

    /// <summary>
    /// 当前版本，只读、不经治理发布器：给只读登记的调用方用（空闲返回的执行，control-server#390，要核验承诺所指的等待点是否仍然合格）。
    /// </summary>
    public static async Task<WaitingPointRegistrationVersion?> ReadCurrentFromAsync(
        ControlServerDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        long? current = await context.Set<WaitingPointVersionRow>()
            .MaxAsync(row => (long?)row.Version, cancellationToken);
        return current is null ? null : await ReadVersionFromAsync(context, current.Value, cancellationToken);
    }

    private static async Task<WaitingPointRegistrationVersion?> ReadVersionFromAsync(
        ControlServerDbContext context, long version, CancellationToken cancellationToken)
    {
        WaitingPointVersionRow? header = await context.Set<WaitingPointVersionRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.Version == version, cancellationToken);
        if (header is null)
        {
            return null;
        }
        WaitingPointRow[] rows = await context.Set<WaitingPointRow>().AsNoTracking()
            .Where(row => row.Version == version)
            .ToArrayAsync(cancellationToken);
        WaitingPointVehicleScopeRow[] scopes = await context.Set<WaitingPointVehicleScopeRow>().AsNoTracking()
            .Where(row => row.Version == version)
            .ToArrayAsync(cancellationToken);
        WaitingPointEntry[] points =
        [
            .. rows.OrderBy(row => row.MapId).ThenBy(row => row.StationId).Select(row => new WaitingPointEntry(
                row.MapId,
                row.StationId,
                row.StationName,
                row.Enabled,
                [
                    .. scopes.Where(scope => scope.MapId == row.MapId && scope.StationId == row.StationId)
                        .Select(scope => scope.VehicleKey)
                        .Order(StringComparer.Ordinal)
                ]))
        ];
        return new WaitingPointRegistrationVersion(
            header.Version, header.ContentSha256, header.SnapshotId, header.LoadedAt, header.Source, points);
    }

    // Ordered so that the same registration always freezes to the same content and the same SHA-256.
    private static WaitingPointEntry[] Normalise(IReadOnlyList<WaitingPointEntry> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        foreach (WaitingPointEntry point in points)
        {
            ArgumentNullException.ThrowIfNull(point, nameof(points));
            ArgumentException.ThrowIfNullOrWhiteSpace(point.StationName, nameof(points));
            ArgumentNullException.ThrowIfNull(point.VehicleScope, nameof(points));
            foreach (string vehicleKey in point.VehicleScope)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey, nameof(points));
            }
        }
        string[] duplicated =
        [
            .. points.GroupBy(point => (point.MapId, point.StationId))
                .Where(group => group.Count() > 1)
                .Select(group => $"{group.Key.MapId}/{group.Key.StationId}")
        ];
        if (duplicated.Length > 0)
        {
            throw new ArgumentException(
                $"A waiting point registration names each station at most once; repeated: {string.Join(", ", duplicated)}.",
                nameof(points));
        }
        return
        [
            .. points.OrderBy(point => point.MapId).ThenBy(point => point.StationId).Select(point => point with
            {
                VehicleScope = [.. point.VehicleScope.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]
            })
        ];
    }

    private static string ContentJson(IEnumerable<WaitingPointEntry> ordered) =>
        JsonSerializer.Serialize(new
        {
            waitingPoints = ordered.Select(point => new
            {
                mapId = point.MapId,
                stationId = point.StationId,
                stationName = point.StationName,
                enabled = point.Enabled,
                vehicleScope = point.VehicleScope
            })
        });

    // The same digest the snapshot store records (GovernanceStore), so an unchanged registration is recognised by it.
    private static string Sha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
