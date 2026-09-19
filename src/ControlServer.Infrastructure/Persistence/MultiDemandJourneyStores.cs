using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 停靠与从属需求的读写（规格 5.2）。写方法各自 SaveChanges；同一需求至多一条未移除的归属由唯一索引保证。
/// </summary>
public sealed class JourneyMembershipStore(ControlServerDbContext context) : IJourneyMembershipStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<string?> FindJourneyIdByDemandAsync(string demandId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        return await _context.Set<JourneyDemandRow>().AsNoTracking()
            .Where(row => row.DemandId == demandId && row.RemovedAt == null)
            .Select(row => row.JourneyId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<JourneyStop>> ListStopsAsync(string journeyId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        JourneyStopRow[] rows = await _context.Set<JourneyStopRow>().AsNoTracking()
            .Where(row => row.JourneyId == journeyId)
            .OrderBy(row => row.Sequence)
            .ToArrayAsync(cancellationToken);
        return [.. rows.Select(ToStop)];
    }

    public async Task<IReadOnlyList<JourneyDemand>> ListDemandsAsync(
        string journeyId, bool includeRemoved, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        JourneyDemandRow[] rows = await _context.Set<JourneyDemandRow>().AsNoTracking()
            .Where(row => row.JourneyId == journeyId && (includeRemoved || row.RemovedAt == null))
            .ToArrayAsync(cancellationToken);
        return [.. rows.Select(ToDemand)];
    }

    public async Task AddStopAsync(JourneyStop journeyStop, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journeyStop);
        _context.Set<JourneyStopRow>().Add(new JourneyStopRow
        {
            StopId = journeyStop.StopId,
            JourneyId = journeyStop.JourneyId,
            Sequence = journeyStop.Sequence,
            StopRole = journeyStop.StopRole,
            StationId = journeyStop.StationId,
            StationRiotId = journeyStop.StationRiotId,
            DispatchZone = journeyStop.DispatchZone,
            OperationSessionId = journeyStop.OperationSessionId,
            MovementLegId = journeyStop.MovementLegId,
            UpperId = journeyStop.UpperId,
            VehicleBusinessMessageId = journeyStop.VehicleBusinessMessageId,
            WorklistMessageId = journeyStop.WorklistMessageId,
            PlanMessageId = journeyStop.PlanMessageId,
            SublotRequestMessageId = journeyStop.SublotRequestMessageId,
            DepartureSafetyCheckMessageId = journeyStop.DepartureSafetyCheckMessageId,
            DepartureSafetyCheckId = journeyStop.DepartureSafetyCheckId,
            Status = journeyStop.Status,
            CreatedAt = journeyStop.CreatedAt
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task AddDemandAsync(JourneyDemand demand, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(demand);
        _context.Set<JourneyDemandRow>().Add(new JourneyDemandRow
        {
            JourneyId = demand.JourneyId,
            DemandId = demand.DemandId,
            PickupStopId = demand.PickupStopId,
            UnloadStopId = demand.UnloadStopId,
            ExpectedBasketCount = demand.ExpectedBasketCount,
            TargetSlotsJson = JsonSerializer.Serialize(demand.TargetSlots),
            LoadedSlotsJson = demand.LoadedSlots is null ? null : JsonSerializer.Serialize(demand.LoadedSlots),
            LoadSlotOperationAttemptId = demand.LoadSlotOperationAttemptId,
            LoadCommandMessageId = demand.LoadCommandMessageId,
            UnloadSlotOperationAttemptId = demand.UnloadSlotOperationAttemptId,
            UnloadCommandMessageId = demand.UnloadCommandMessageId,
            DispatchZone = demand.DispatchZone,
            DispatchGeneration = demand.DispatchGeneration,
            Status = demand.Status,
            AddedAt = demand.AddedAt,
            RemovedAt = demand.RemovedAt,
            RemovalReason = demand.RemovalReason,
            DispatchZoneParameterVersion = demand.DispatchZoneParameterVersion
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task SetStopStatusAsync(string stopId, string status, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        (await StopAsync(stopId, cancellationToken)).Status = status;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task SetStopSequenceAsync(string stopId, int sequence, CancellationToken cancellationToken)
    {
        (await StopAsync(stopId, cancellationToken)).Sequence = sequence;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveDemandAsync(
        string journeyId, string demandId, string removalReason, DateTimeOffset removedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(removalReason);
        JourneyDemandRow row = await _context.Set<JourneyDemandRow>()
            .SingleOrDefaultAsync(item => item.JourneyId == journeyId && item.DemandId == demandId, cancellationToken)
            ?? throw new InvalidOperationException($"Demand '{demandId}' does not belong to journey '{journeyId}'.");
        if (row.RemovedAt is not null)
        {
            return;
        }
        row.RemovedAt = removedAt;
        row.RemovalReason = removalReason;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<JourneyStopRow> StopAsync(string stopId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stopId);
        return await _context.Set<JourneyStopRow>().SingleOrDefaultAsync(row => row.StopId == stopId, cancellationToken)
            ?? throw new InvalidOperationException($"Stop '{stopId}' does not exist.");
    }

    private static JourneyStop ToStop(JourneyStopRow row) => new(
        row.StopId, row.JourneyId, row.Sequence, row.StopRole, row.StationId, row.StationRiotId, row.DispatchZone,
        row.OperationSessionId, row.MovementLegId, row.UpperId, row.VehicleBusinessMessageId, row.WorklistMessageId,
        row.PlanMessageId, row.SublotRequestMessageId, row.DepartureSafetyCheckMessageId, row.DepartureSafetyCheckId,
        row.Status, row.CreatedAt);

    private static JourneyDemand ToDemand(JourneyDemandRow row) => new(
        row.JourneyId, row.DemandId, row.PickupStopId, row.UnloadStopId, row.ExpectedBasketCount,
        JsonSerializer.Deserialize<int[]>(row.TargetSlotsJson) ?? [],
        row.LoadedSlotsJson is null ? null : JsonSerializer.Deserialize<int[]>(row.LoadedSlotsJson),
        row.LoadSlotOperationAttemptId, row.LoadCommandMessageId, row.UnloadSlotOperationAttemptId,
        row.UnloadCommandMessageId, row.DispatchZone, row.DispatchGeneration, row.Status, row.AddedAt, row.RemovedAt,
        row.RemovalReason, row.DispatchZoneParameterVersion);
}

// <summary>
/// 车辆用途占有。谁占到由 <c>VehiclePurposeClaims</c> 的主键决定：先插入，被主键拒了再看占着的是谁。
/// </summary>
public sealed class VehiclePurposeClaimStore(ControlServerDbContext context) : IVehiclePurposeClaimStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<VehiclePurposeClaim?> ReadAsync(string vehicleKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        VehiclePurposeClaimRow? row = await _context.Set<VehiclePurposeClaimRow>().AsNoTracking()
            .SingleOrDefaultAsync(claim => claim.VehicleKey == vehicleKey, cancellationToken);
        return row is null ? null : new VehiclePurposeClaim(row.VehicleKey, row.Purpose, row.JourneyId, row.ClaimedAt);
    }

    public async Task<bool> TryClaimAsync(VehiclePurposeClaim claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentException.ThrowIfNullOrWhiteSpace(claim.VehicleKey, nameof(claim));
        ArgumentException.ThrowIfNullOrWhiteSpace(claim.Purpose, nameof(claim));
        ArgumentException.ThrowIfNullOrWhiteSpace(claim.JourneyId, nameof(claim));

        ForgetUnchanged(claim.VehicleKey);
        VehiclePurposeClaimRow row = new()
        {
            VehicleKey = claim.VehicleKey,
            Purpose = claim.Purpose,
            JourneyId = claim.JourneyId,
            ClaimedAt = claim.ClaimedAt
        };
        _context.Set<VehiclePurposeClaimRow>().Add(row);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException failure) when (failure.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            // The key refused the insert: someone holds the vehicle. Only who it is decides the answer.
            _context.Entry(row).State = EntityState.Detached;
            VehiclePurposeClaim? holder = await ReadAsync(claim.VehicleKey, cancellationToken);
            return holder is not null && holder.JourneyId == claim.JourneyId && holder.Purpose == claim.Purpose;
        }
    }

    public async Task ReleaseAsync(string vehicleKey, string journeyId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        VehiclePurposeClaimRow? row = await _context.Set<VehiclePurposeClaimRow>()
            .SingleOrDefaultAsync(claim => claim.VehicleKey == vehicleKey && claim.JourneyId == journeyId, cancellationToken);
        if (row is null)
        {
            return;
        }
        _context.Set<VehiclePurposeClaimRow>().Remove(row);
        await _context.SaveChangesAsync(cancellationToken);
    }

    // See WireToGateStore.ForgetClaimsThisContextLastSaw: a claim this context saw earlier may since have been released
    // through another context, and a second tracked instance with its key is refused in memory.
    private void ForgetUnchanged(string vehicleKey)
    {
        foreach (var stale in _context.ChangeTracker.Entries<VehiclePurposeClaimRow>()
                     .Where(entry => entry.State == EntityState.Unchanged && entry.Entity.VehicleKey == vehicleKey)
                     .ToArray())
        {
            stale.State = EntityState.Detached;
        }
    }
}

/// <summary>
/// 按业务键抑制。只追加：先插入，主键拒了就读回键上那一条；没有改写或删除的方法。
/// </summary>
public sealed class TransportDemandSuppressionStore(ControlServerDbContext context) : ITransportDemandSuppressionStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<TransportDemandSuppression> SuppressIfAbsentAsync(
        TransportDemandSuppression suppression, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(suppression);
        ArgumentException.ThrowIfNullOrWhiteSpace(suppression.TransportDemandKey, nameof(suppression));
        ArgumentException.ThrowIfNullOrWhiteSpace(suppression.DemandId, nameof(suppression));
        ArgumentException.ThrowIfNullOrWhiteSpace(suppression.ReasonCode, nameof(suppression));

        // A suppression is never rewritten or deleted, so one this context already holds is the standing one.
        TransportDemandSuppressionRow? known = _context.Set<TransportDemandSuppressionRow>().Local
            .SingleOrDefault(row => row.TransportDemandKey == suppression.TransportDemandKey);
        if (known is not null)
        {
            return ToSuppression(known);
        }

        TransportDemandSuppressionRow row = new()
        {
            TransportDemandKey = suppression.TransportDemandKey,
            DemandId = suppression.DemandId,
            ReasonCode = suppression.ReasonCode,
            SuppressedAt = suppression.SuppressedAt
        };
        _context.Set<TransportDemandSuppressionRow>().Add(row);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return suppression;
        }
        catch (DbUpdateException failure) when (failure.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            // Another writer suppressed the key first. Its suppression stands; this one is dropped, not an error.
            _context.Entry(row).State = EntityState.Detached;
            return await ReadAsync(suppression.TransportDemandKey, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Suppressing '{suppression.TransportDemandKey}' was refused, yet no suppression of it exists.");
        }
    }

    public async Task<TransportDemandSuppression?> ReadAsync(string transportDemandKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transportDemandKey);
        TransportDemandSuppressionRow? row = await _context.Set<TransportDemandSuppressionRow>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.TransportDemandKey == transportDemandKey, cancellationToken);
        return row is null ? null : ToSuppression(row);
    }

    private static TransportDemandSuppression ToSuppression(TransportDemandSuppressionRow row) =>
        new(row.TransportDemandKey, row.DemandId, row.ReasonCode, row.SuppressedAt);
}

/// <summary>
/// 每区派车参数的版本存取（REQ-0198、REQ-0203），形状照 <see cref="AreaAssignmentStore"/>。
/// </summary>
/// <remarks>
/// 每写一版都经 <see cref="GovernedConfigurationPublisher"/> 冻结整表快照并写业务审计，与版本行、分区行在同一个事务里。版本号取
/// 当前最大 + 1，两个并发写入者撞在主键上，一方整体回滚。版本行与分区行写入即不可改，由 <see cref="PublishedVersionImmutabilityGuard"/>
/// 在上下文上拦。L2 编排器的预置不走这里（scripts/l2/L2DispatchZoneParameters.psm1 直写库，<c>Source</c> 记
/// <see cref="DispatchZoneParameterSources.L2Preset"/>）；正式导入的动词在批次7-11（control-server#216）。
/// </remarks>
public sealed class DispatchZoneParameterStore(
    ControlServerDbContext context,
    GovernedConfigurationPublisher publisher) : IDispatchZoneParameterStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly GovernedConfigurationPublisher _publisher =
        publisher ?? throw new ArgumentNullException(nameof(publisher));

    public async Task<DispatchZoneParameterTableVersion> WriteVersionAsync(
        IReadOnlyList<DispatchZoneParameters> zones, DateTimeOffset loadedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zones);
        foreach (DispatchZoneParameters zone in zones)
        {
            ArgumentNullException.ThrowIfNull(zone, nameof(zones));
            ArgumentException.ThrowIfNullOrWhiteSpace(zone.DispatchZone, nameof(zones));
            if (zone.EnRouteAdditionMaxPathCostIncrease < 0 || zone.StarvationThresholdSeconds < 0)
            {
                throw new ArgumentException(
                    $"Dispatch zone '{zone.DispatchZone}' carries a negative parameter; leave it empty for unconfigured.",
                    nameof(zones));
            }
        }
        string[] duplicated =
        [
            .. zones.GroupBy(zone => zone.DispatchZone, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .Order(StringComparer.Ordinal)
        ];
        if (duplicated.Length > 0)
        {
            throw new ArgumentException(
                $"A dispatch zone parameter table names each zone at most once; repeated: {string.Join(", ", duplicated)}.",
                nameof(zones));
        }

        // Ordered so that the same table always freezes to the same content and the same SHA-256.
        DispatchZoneParameters[] ordered = [.. zones.OrderBy(zone => zone.DispatchZone, StringComparer.Ordinal)];

        await using IDbContextTransaction? transaction = _context.Database.CurrentTransaction is null
            ? await _context.Database.BeginTransactionAsync(cancellationToken)
            : null;

        long version = (await _context.Set<DispatchZoneParameterVersionRow>()
            .MaxAsync(row => (long?)row.Version, cancellationToken) ?? 0) + 1;
        GovernedConfigurationSnapshot snapshot = await _publisher.PublishVersionAsync(
            GovernedObjectKind.DispatchZoneParameters,
            DispatchZoneParameterGovernance.ObjectId,
            version,
            ContentJson(ordered),
            DispatchZoneParameterGovernance.VersionImportedAction,
            loadedAt,
            cancellationToken);

        _context.Set<DispatchZoneParameterVersionRow>().Add(new DispatchZoneParameterVersionRow
        {
            Version = version,
            ContentSha256 = snapshot.ContentSha256,
            SnapshotId = snapshot.SnapshotId,
            LoadedAt = loadedAt,
            Source = DispatchZoneParameterSources.GovernedImport
        });
        _context.Set<DispatchZoneParameterRow>().AddRange(ordered.Select(zone => new DispatchZoneParameterRow
        {
            Version = version,
            DispatchZone = zone.DispatchZone,
            EnRouteAdditionMaxPathCostIncrease = zone.EnRouteAdditionMaxPathCostIncrease,
            StarvationThresholdSeconds = zone.StarvationThresholdSeconds
        }));
        await _context.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new DispatchZoneParameterTableVersion(
            version,
            snapshot.ContentSha256,
            snapshot.SnapshotId,
            loadedAt,
            DispatchZoneParameterSources.GovernedImport,
            ordered.ToDictionary(zone => zone.DispatchZone, StringComparer.Ordinal));
    }

    public async Task<DispatchZoneParameterTableVersion?> ReadCurrentAsync(CancellationToken cancellationToken)
    {
        long? current = await _context.Set<DispatchZoneParameterVersionRow>()
            .MaxAsync(row => (long?)row.Version, cancellationToken);
        return current is null ? null : await ReadVersionAsync(current.Value, cancellationToken);
    }

    public async Task<DispatchZoneParameterTableVersion?> ReadVersionAsync(long version, CancellationToken cancellationToken)
    {
        DispatchZoneParameterVersionRow? header = await _context.Set<DispatchZoneParameterVersionRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Version == version, cancellationToken);
        if (header is null)
        {
            return null;
        }

        DispatchZoneParameterRow[] rows = await _context.Set<DispatchZoneParameterRow>()
            .AsNoTracking()
            .Where(row => row.Version == version)
            .ToArrayAsync(cancellationToken);
        return new DispatchZoneParameterTableVersion(
            header.Version,
            header.ContentSha256,
            header.SnapshotId,
            header.LoadedAt,
            header.Source,
            rows.ToDictionary(
                row => row.DispatchZone,
                row => new DispatchZoneParameters(
                    row.DispatchZone, row.EnRouteAdditionMaxPathCostIncrease, row.StarvationThresholdSeconds),
                StringComparer.Ordinal));
    }

    private static string ContentJson(IEnumerable<DispatchZoneParameters> ordered) =>
        JsonSerializer.Serialize(new
        {
            zones = ordered.Select(zone => new
            {
                dispatchZone = zone.DispatchZone,
                enRouteAdditionMaxPathCostIncrease = zone.EnRouteAdditionMaxPathCostIncrease,
                starvationThresholdSeconds = zone.StarvationThresholdSeconds
            })
        });
}

// <summary>
/// 按车修订号计数器。只进不退：任何一条流往回走都拒绝，计数器不变。
/// </summary>
public sealed class VehicleSnapshotRevisionStore(ControlServerDbContext context) : IVehicleSnapshotRevisionStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<VehicleSnapshotRevisions?> ReadAsync(string agvId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        VehicleSnapshotRevisionRow? row = await _context.Set<VehicleSnapshotRevisionRow>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.AgvId == agvId, cancellationToken);
        return row is null
            ? null
            : new VehicleSnapshotRevisions(row.AgvId, row.VehicleBusinessRevision, row.WorklistRevision, row.PlanRevision);
    }

    public async Task AdvanceAsync(VehicleSnapshotRevisions revisions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisions.AgvId, nameof(revisions));

        VehicleSnapshotRevisionRow? row = await _context.Set<VehicleSnapshotRevisionRow>()
            .SingleOrDefaultAsync(item => item.AgvId == revisions.AgvId, cancellationToken);
        if (row is null)
        {
            row = new VehicleSnapshotRevisionRow { AgvId = revisions.AgvId };
            _context.Set<VehicleSnapshotRevisionRow>().Add(row);
        }
        else if (revisions.VehicleBusiness < row.VehicleBusinessRevision ||
                 revisions.Worklist < row.WorklistRevision ||
                 revisions.Plan < row.PlanRevision)
        {
            // The onboard journals the revision it adopted per snapshot type and refuses a lower one as
            // SNAPSHOT_REVISION_REGRESSION, which tears the session down (ADR-cross-0048).
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"Snapshot revisions of {revisions.AgvId} cannot step back: held {row.VehicleBusinessRevision}/{row.WorklistRevision}/{row.PlanRevision}, asked {revisions.VehicleBusiness}/{revisions.Worklist}/{revisions.Plan}."));
        }
        row.VehicleBusinessRevision = revisions.VehicleBusiness;
        row.WorklistRevision = revisions.Worklist;
        row.PlanRevision = revisions.Plan;
        await _context.SaveChangesAsync(cancellationToken);
    }
}
