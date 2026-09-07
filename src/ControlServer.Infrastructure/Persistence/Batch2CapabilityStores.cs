using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// Storage access for the four batch-2 track-B capability lanes (ticket 06).
/// </summary>
/// <remarks>
/// Access only. Nothing here decides anything — no staleness verdict, no admission rule, no
/// escalation. Each lane's own ticket owns its behaviour and needs neither a migration nor a
/// <c>Ports.cs</c> change to add it.
/// </remarks>
public sealed class VehicleDispatchPolicyStore(ControlServerDbContext dbContext) : IVehicleDispatchPolicyStore
{
    public async Task<VehicleDispatchPolicy> ReadPolicyAsync(CancellationToken cancellationToken)
    {
        List<VehicleTaskTypeAdmissionRow> admissions = await dbContext.VehicleTaskTypeAdmissions
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<VehicleDispatchBudgetRow> budgets = await dbContext.VehicleDispatchBudgets
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<DispatchZoneVehicleRow> zones = await dbContext.DispatchZoneVehicles
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<string, HashSet<string>> allowedByVehicle = new(StringComparer.Ordinal);
        foreach (VehicleTaskTypeAdmissionRow row in admissions.Where(row => row.Allowed))
        {
            if (!allowedByVehicle.TryGetValue(row.AgvId, out HashSet<string>? set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                allowedByVehicle[row.AgvId] = set;
            }

            set.Add(row.TaskType);
        }

        // A vehicle with a budget but no admissions is a real configuration — it is a vehicle
        // allowed to take nothing — so the vehicle list is the union of both tables, not just
        // the budget table.
        List<VehicleDispatchProfile> vehicles = budgets
            .Select(row => row.AgvId)
            .Union(admissions.Select(row => row.AgvId), StringComparer.Ordinal)
            .OrderBy(agvId => agvId, StringComparer.Ordinal)
            .Select(agvId => new VehicleDispatchProfile(
                agvId,
                allowedByVehicle.TryGetValue(agvId, out HashSet<string>? allowed)
                    ? allowed
                    : new HashSet<string>(StringComparer.Ordinal),
                budgets.SingleOrDefault(budget => budget.AgvId == agvId)?.RoundTimeoutMilliseconds ?? 0))
            .ToList();

        Dictionary<string, IReadOnlySet<string>> zoneVehicles = zones
            .GroupBy(row => row.Zone, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlySet<string>)group
                    .Select(row => row.AgvId)
                    .ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        string configurationVersion =
            admissions.Select(row => row.ConfigurationVersion)
                .Concat(budgets.Select(row => row.ConfigurationVersion))
                .Concat(zones.Select(row => row.ConfigurationVersion))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(version => version, StringComparer.Ordinal)
                .FirstOrDefault() ?? "";

        return new VehicleDispatchPolicy(vehicles, zoneVehicles, configurationVersion);
    }

    public async Task ReplacePolicyAsync(
        VehicleDispatchPolicy policy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);

        dbContext.VehicleTaskTypeAdmissions.RemoveRange(dbContext.VehicleTaskTypeAdmissions);
        dbContext.VehicleDispatchBudgets.RemoveRange(dbContext.VehicleDispatchBudgets);
        dbContext.DispatchZoneVehicles.RemoveRange(dbContext.DispatchZoneVehicles);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (VehicleDispatchProfile vehicle in policy.Vehicles)
        {
            dbContext.VehicleDispatchBudgets.Add(new VehicleDispatchBudgetRow
            {
                AgvId = vehicle.AgvId,
                RoundTimeoutMilliseconds = vehicle.RoundTimeoutMilliseconds,
                ConfigurationVersion = policy.ConfigurationVersion,
                UpdatedAt = updatedAt,
            });

            foreach (string taskType in vehicle.AllowedTaskTypes)
            {
                dbContext.VehicleTaskTypeAdmissions.Add(new VehicleTaskTypeAdmissionRow
                {
                    AgvId = vehicle.AgvId,
                    TaskType = taskType,
                    Allowed = true,
                    ConfigurationVersion = policy.ConfigurationVersion,
                    UpdatedAt = updatedAt,
                });
            }
        }

        foreach ((string zone, IReadOnlySet<string> agvIds) in policy.ZoneVehicles)
        {
            foreach (string agvId in agvIds)
            {
                dbContext.DispatchZoneVehicles.Add(new DispatchZoneVehicleRow
                {
                    Zone = zone,
                    AgvId = agvId,
                    ConfigurationVersion = policy.ConfigurationVersion,
                    UpdatedAt = updatedAt,
                });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryClaimVehicleOccupancyAsync(
        string upperId,
        DateTimeOffset claimedAt,
        CancellationToken cancellationToken)
    {
        OrderIntentRow row = await dbContext.OrderIntents
            .SingleAsync(intent => intent.UpperId == upperId, cancellationToken)
            .ConfigureAwait(false);

        row.VehicleOccupancyClaimedAt = claimedAt;
        row.VehicleOccupancyReleasedAt = null;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            // The unique index decided, not a read-then-write: another order already occupies
            // this vehicle. Detaching leaves the context usable for the rest of the round.
            dbContext.Entry(row).State = EntityState.Detached;
            return false;
        }
    }

    public async Task ReleaseVehicleOccupancyAsync(
        string upperId,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken)
    {
        OrderIntentRow row = await dbContext.OrderIntents
            .SingleAsync(intent => intent.UpperId == upperId, cancellationToken)
            .ConfigureAwait(false);

        row.VehicleOccupancyReleasedAt = releasedAt;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Storage for the <c>RouteGraphSnapshot</c> engine's two refresh cycles (ticket 12).</summary>
public sealed class RouteGraphSnapshotStore(ControlServerDbContext dbContext) : IRouteGraphSnapshotStore
{
    public async Task<RouteGraphSnapshotHeader?> ReadHeaderAsync(int mapId, CancellationToken cancellationToken)
    {
        RouteGraphSnapshotRow? row = await dbContext.RouteGraphSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(snapshot => snapshot.MapId == mapId, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ToHeader(row);
    }

    public async Task<RouteGraphState?> ReadStateAsync(int mapId, CancellationToken cancellationToken)
    {
        RouteGraphSnapshotRow? header = await dbContext.RouteGraphSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(snapshot => snapshot.MapId == mapId, cancellationToken)
            .ConfigureAwait(false);
        if (header is null)
        {
            return null;
        }

        List<RouteGraphEdgeFact> edges = await dbContext.RouteGraphEdges
            .AsNoTracking()
            .Where(edge => edge.MapId == mapId)
            .OrderBy(edge => edge.EdgeId)
            .Select(edge => new RouteGraphEdgeFact(
                edge.EdgeId,
                edge.StartNode,
                edge.EndNode,
                edge.CostMm,
                edge.StartX,
                edge.StartY,
                edge.EndX,
                edge.EndY,
                edge.Direction,
                edge.IsBackEdge))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<RouteGraphStationFact> stations = await dbContext.RouteGraphStations
            .AsNoTracking()
            .Where(station => station.MapId == mapId)
            .OrderBy(station => station.StationId)
            .Select(station => new RouteGraphStationFact(
                station.StationId,
                station.Name,
                station.EdgeId,
                station.PosX,
                station.PosY,
                station.ResolvedNode,
                station.ResolutionResidualMm))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<int> removedEdges = await dbContext.RouteGraphRemovedEdges
            .AsNoTracking()
            .Where(removed => removed.MapId == mapId)
            .Select(removed => removed.EdgeId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<int> removedStations = await dbContext.RouteGraphRemovedStations
            .AsNoTracking()
            .Where(removed => removed.MapId == mapId)
            .Select(removed => removed.StationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new RouteGraphState(
            ToHeader(header),
            edges,
            stations,
            removedEdges.ToHashSet(),
            removedStations.ToHashSet());
    }

    public async Task<long> ReplaceDesignStateAsync(
        int mapId,
        IReadOnlyList<RouteGraphEdgeFact> edges,
        IReadOnlyList<RouteGraphStationFact> stations,
        string? sourceGmtUpdate,
        DateTimeOffset refreshedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(stations);

        RouteGraphSnapshotRow row = await GetOrCreateHeaderAsync(mapId, cancellationToken).ConfigureAwait(false);
        long revision = row.DesignRevision + 1;

        // Wholesale replacement: the edge table is a snapshot, not a log. A partially applied
        // graph is a graph with holes, and a hole reads as a wrong shortest path rather than as
        // a visible failure.
        dbContext.RouteGraphEdges.RemoveRange(
            dbContext.RouteGraphEdges.Where(edge => edge.MapId == mapId));
        dbContext.RouteGraphStations.RemoveRange(
            dbContext.RouteGraphStations.Where(station => station.MapId == mapId));

        foreach (RouteGraphEdgeFact edge in edges)
        {
            dbContext.RouteGraphEdges.Add(new RouteGraphEdgeRow
            {
                MapId = mapId,
                EdgeId = edge.EdgeId,
                StartNode = edge.StartNode,
                EndNode = edge.EndNode,
                CostMm = edge.CostMm,
                StartX = edge.StartX,
                StartY = edge.StartY,
                EndX = edge.EndX,
                EndY = edge.EndY,
                Direction = edge.Direction,
                IsBackEdge = edge.IsBackEdge,
                DesignRevision = revision,
            });
        }

        foreach (RouteGraphStationFact station in stations)
        {
            dbContext.RouteGraphStations.Add(new RouteGraphStationRow
            {
                MapId = mapId,
                StationId = station.StationId,
                Name = station.Name,
                EdgeId = station.EdgeId,
                PosX = station.PosX,
                PosY = station.PosY,
                ResolvedNode = station.ResolvedNode,
                ResolutionResidualMm = station.ResolutionResidualMm,
                DesignRevision = revision,
            });
        }

        row.DesignRevision = revision;
        row.DesignRefreshedAt = refreshedAt;
        row.DesignSourceGmtUpdate = sourceGmtUpdate;
        row.DesignEdgeCount = edges.Count;
        row.DesignStationCount = stations.Count;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return revision;
    }

    public async Task ReplaceRuntimeStateAsync(
        int mapId,
        IReadOnlyList<int> removedEdgeIds,
        IReadOnlyList<int> removedStationIds,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(removedEdgeIds);
        ArgumentNullException.ThrowIfNull(removedStationIds);

        RouteGraphSnapshotRow row = await GetOrCreateHeaderAsync(mapId, cancellationToken).ConfigureAwait(false);

        dbContext.RouteGraphRemovedEdges.RemoveRange(
            dbContext.RouteGraphRemovedEdges.Where(removed => removed.MapId == mapId));
        dbContext.RouteGraphRemovedStations.RemoveRange(
            dbContext.RouteGraphRemovedStations.Where(removed => removed.MapId == mapId));

        foreach (int edgeId in removedEdgeIds.Distinct())
        {
            dbContext.RouteGraphRemovedEdges.Add(new RouteGraphRemovedEdgeRow
            {
                MapId = mapId,
                EdgeId = edgeId,
                ObservedAt = observedAt,
            });
        }

        foreach (int stationId in removedStationIds.Distinct())
        {
            dbContext.RouteGraphRemovedStations.Add(new RouteGraphRemovedStationRow
            {
                MapId = mapId,
                StationId = stationId,
                ObservedAt = observedAt,
            });
        }

        row.RuntimeRefreshedAt = observedAt;
        row.RuntimeRemovedEdgeCount = removedEdgeIds.Distinct().Count();
        row.RuntimeRemovedStationCount = removedStationIds.Distinct().Count();

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceEdgeGroupsAsync(
        int mapId,
        IReadOnlyList<RouteGraphEdgeGroupFact> groups,
        string fingerprint,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(fingerprint);

        RouteGraphSnapshotRow row = await GetOrCreateHeaderAsync(mapId, cancellationToken).ConfigureAwait(false);

        dbContext.RouteGraphEdgeGroups.RemoveRange(
            dbContext.RouteGraphEdgeGroups.Where(group => group.MapId == mapId));

        foreach (RouteGraphEdgeGroupFact group in groups)
        {
            dbContext.RouteGraphEdgeGroups.Add(new RouteGraphEdgeGroupRow
            {
                MapId = mapId,
                GroupName = group.GroupName,
                EdgeId = group.EdgeId,
                GroupType = group.GroupType,
                ObservedAt = observedAt,
            });
        }

        row.EdgeGroupFingerprint = fingerprint;
        row.EdgeGroupRefreshedAt = observedAt;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordDynamicRouteCostObservationAsync(
        int mapId,
        bool present,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        RouteGraphSnapshotRow row = await GetOrCreateHeaderAsync(mapId, cancellationToken).ConfigureAwait(false);
        row.DynamicRouteCostPresent = present;
        row.DynamicRouteCostObservedAt = observedAt;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkStaleAsync(
        int mapId,
        string reason,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        RouteGraphSnapshotRow row = await GetOrCreateHeaderAsync(mapId, cancellationToken).ConfigureAwait(false);
        // First reason wins: StaleSince has to answer "since when", and a second trigger arriving
        // while already stale must not restart that clock.
        if (row.StaleReason is null)
        {
            row.StaleSince = at;
        }

        row.StaleReason = reason;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearStaleAsync(int mapId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        _ = at;
        RouteGraphSnapshotRow row = await GetOrCreateHeaderAsync(mapId, cancellationToken).ConfigureAwait(false);
        row.StaleReason = null;
        row.StaleSince = null;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<RouteGraphSnapshotRow> GetOrCreateHeaderAsync(int mapId, CancellationToken cancellationToken)
    {
        RouteGraphSnapshotRow? row = await dbContext.RouteGraphSnapshots
            .SingleOrDefaultAsync(snapshot => snapshot.MapId == mapId, cancellationToken)
            .ConfigureAwait(false);
        if (row is not null)
        {
            return row;
        }

        // A new header starts stale: nothing has been fetched, and "no snapshot" must never read
        // as "a usable empty graph".
        row = new RouteGraphSnapshotRow
        {
            MapId = mapId,
            DesignRevision = 0,
            EdgeGroupFingerprint = "",
            StaleReason = "SNAPSHOT_NEVER_REFRESHED",
            StaleSince = DateTimeOffset.MinValue,
        };
        dbContext.RouteGraphSnapshots.Add(row);
        return row;
    }

    private static RouteGraphSnapshotHeader ToHeader(RouteGraphSnapshotRow row) => new(
        row.MapId,
        row.DesignRevision,
        row.DesignRefreshedAt,
        row.DesignSourceGmtUpdate,
        row.DesignEdgeCount,
        row.DesignStationCount,
        row.RuntimeRefreshedAt,
        row.RuntimeRemovedEdgeCount,
        row.RuntimeRemovedStationCount,
        row.EdgeGroupFingerprint,
        row.EdgeGroupRefreshedAt,
        row.DynamicRouteCostPresent,
        row.DynamicRouteCostObservedAt,
        row.StaleReason,
        row.StaleSince);
}

/// <summary>Storage for the two-level fault model and its cargo bindings (tickets 10 and 11).</summary>
public sealed class VehicleFaultStore(ControlServerDbContext dbContext) : IVehicleFaultStore
{
    public async Task<VehicleFaultFact?> ReadAsync(string agvId, CancellationToken cancellationToken)
    {
        VehicleFaultStateRow? row = await dbContext.VehicleFaultStates
            .AsNoTracking()
            .SingleOrDefaultAsync(state => state.AgvId == agvId, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ToFact(row);
    }

    public async Task<VehicleFaultFact> RecordLevelAsync(
        string agvId,
        VehicleFaultLevel level,
        string evidenceCode,
        bool evidenceOnAutoConfirmWhitelist,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceCode);

        VehicleFaultStateRow? row = await dbContext.VehicleFaultStates
            .SingleOrDefaultAsync(state => state.AgvId == agvId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new VehicleFaultStateRow
            {
                AgvId = agvId,
                Level = VehicleFaultLevel.None,
                FaultGeneration = 0,
            };
            dbContext.VehicleFaultStates.Add(row);
        }

        // Entering from None starts a new episode; escalating within a live fault keeps the
        // generation, so the cargo binding and the command audit stay attached to the same one.
        bool startsNewEpisode = row.Level == VehicleFaultLevel.None && level != VehicleFaultLevel.None;
        if (startsNewEpisode)
        {
            row.FaultGeneration += 1;
            row.EnteredAt = at;
            row.StopProven = false;
            row.StopProvenAt = null;
            row.EscalatedAt = null;
            row.ClearedAt = null;
            row.ClearedReason = null;
        }

        row.Level = level;
        row.EvidenceCode = evidenceCode;
        row.EvidenceOnAutoConfirmWhitelist = evidenceOnAutoConfirmWhitelist;
        row.LastEvaluatedAt = at;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToFact(row);
    }

    public async Task RecordStopProofAsync(
        string agvId,
        long faultGeneration,
        bool proven,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        VehicleFaultStateRow row = await RequireGenerationAsync(agvId, faultGeneration, cancellationToken)
            .ConfigureAwait(false);

        row.StopProven = proven;
        row.StopProvenAt = proven ? at : null;
        row.LastEvaluatedAt = at;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordEscalationAsync(
        string agvId,
        long faultGeneration,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        VehicleFaultStateRow row = await RequireGenerationAsync(agvId, faultGeneration, cancellationToken)
            .ConfigureAwait(false);

        row.EscalatedAt = at;
        row.LastEvaluatedAt = at;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(
        string agvId,
        long faultGeneration,
        string reason,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        VehicleFaultStateRow row = await RequireGenerationAsync(agvId, faultGeneration, cancellationToken)
            .ConfigureAwait(false);

        row.Level = VehicleFaultLevel.None;
        row.ClearedAt = at;
        row.ClearedReason = reason;
        row.LastEvaluatedAt = at;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FaultedCargoBinding> BindCargoAsync(
        string agvId,
        long faultGeneration,
        string demandId,
        string? movementLegId,
        string transportDemandKey,
        bool loadingWitnessed,
        DateTimeOffset boundAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(transportDemandKey);

        FaultedVehicleCargoRow row = new()
        {
            CargoBindingId = Guid.NewGuid().ToString("N"),
            AgvId = agvId,
            FaultGeneration = faultGeneration,
            DemandId = demandId,
            MovementLegId = movementLegId,
            TransportDemandKey = transportDemandKey,
            LoadingWitnessed = loadingWitnessed,
            BoundAt = boundAt,
        };

        dbContext.FaultedVehicleCargo.Add(row);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToBinding(row);
    }

    public async Task<FaultedCargoBinding?> ReadLiveCargoAsync(string agvId, CancellationToken cancellationToken)
    {
        FaultedVehicleCargoRow? row = await dbContext.FaultedVehicleCargo
            .AsNoTracking()
            .SingleOrDefaultAsync(
                cargo => cargo.AgvId == agvId && cargo.ReleasedAt == null,
                cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ToBinding(row);
    }

    public async Task ReleaseCargoAsync(
        string cargoBindingId,
        string reason,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        FaultedVehicleCargoRow row = await dbContext.FaultedVehicleCargo
            .SingleAsync(cargo => cargo.CargoBindingId == cargoBindingId, cancellationToken)
            .ConfigureAwait(false);

        row.ReleasedAt = releasedAt;
        row.ReleasedReason = reason;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<VehicleFaultStateRow> RequireGenerationAsync(
        string agvId,
        long faultGeneration,
        CancellationToken cancellationToken)
    {
        VehicleFaultStateRow row = await dbContext.VehicleFaultStates
            .SingleAsync(state => state.AgvId == agvId, cancellationToken)
            .ConfigureAwait(false);

        // A write carrying a stale generation belongs to an episode that has already ended.
        // Applying it would attach one episode's evidence to another's fault.
        if (row.FaultGeneration != faultGeneration)
        {
            throw new InvalidOperationException(
                $"Vehicle {agvId} is at fault generation {row.FaultGeneration}, not {faultGeneration}.");
        }

        return row;
    }

    private static VehicleFaultFact ToFact(VehicleFaultStateRow row) => new(
        row.AgvId,
        row.Level,
        row.FaultGeneration,
        row.EvidenceCode,
        row.EvidenceOnAutoConfirmWhitelist,
        row.EnteredAt,
        row.LastEvaluatedAt,
        row.StopProven,
        row.StopProvenAt,
        row.EscalatedAt,
        row.ClearedAt,
        row.ClearedReason);

    private static FaultedCargoBinding ToBinding(FaultedVehicleCargoRow row) => new(
        row.CargoBindingId,
        row.AgvId,
        row.FaultGeneration,
        row.DemandId,
        row.MovementLegId,
        row.TransportDemandKey,
        row.LoadingWitnessed,
        row.BoundAt,
        row.ReleasedAt,
        row.ReleasedReason);
}

/// <summary>Storage for RIoT order-command attempts and their reconciliation (ticket 10).</summary>
public sealed class RiotOrderCommandAuditStore(ControlServerDbContext dbContext) : IRiotOrderCommandAuditStore
{
    public async Task<RiotOrderCommandAttempt> ArmAttemptAsync(
        string commandType,
        string agvId,
        string targetUpperId,
        string? targetOrderId,
        string requestSemanticSha256,
        long? faultGeneration,
        DateTimeOffset issuedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandType);
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUpperId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestSemanticSha256);

        int previousAttempts = await dbContext.RiotOrderCommandAudit
            .CountAsync(
                audit => audit.CommandType == commandType && audit.TargetUpperId == targetUpperId,
                cancellationToken)
            .ConfigureAwait(false);

        RiotOrderCommandAuditRow row = new()
        {
            CommandAuditId = Guid.NewGuid().ToString("N"),
            CommandType = commandType,
            AgvId = agvId,
            TargetUpperId = targetUpperId,
            TargetOrderId = targetOrderId,
            AttemptNumber = previousAttempts + 1,
            RequestSemanticSha256 = requestSemanticSha256,
            IssuedAt = issuedAt,
            // Armed, not sent. An attempt sitting at Pending is exactly the state that says
            // "this may have gone out and we do not know what happened to it".
            Outcome = RiotOrderCommandOutcome.Pending,
            FaultGeneration = faultGeneration,
        };

        dbContext.RiotOrderCommandAudit.Add(row);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToAttempt(row);
    }

    public async Task RecordOutcomeAsync(
        string commandAuditId,
        RiotOrderCommandOutcome outcome,
        string? receiptJson,
        DateTimeOffset reconciledAt,
        CancellationToken cancellationToken)
    {
        RiotOrderCommandAuditRow row = await dbContext.RiotOrderCommandAudit
            .SingleAsync(audit => audit.CommandAuditId == commandAuditId, cancellationToken)
            .ConfigureAwait(false);

        row.Outcome = outcome;
        row.ReceiptJson = receiptJson;
        row.ReconciledAt = reconciledAt;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RiotOrderCommandAttempt>> ReadAttemptsAsync(
        string commandType,
        string targetUpperId,
        CancellationToken cancellationToken)
    {
        List<RiotOrderCommandAuditRow> rows = await dbContext.RiotOrderCommandAudit
            .AsNoTracking()
            .Where(audit => audit.CommandType == commandType && audit.TargetUpperId == targetUpperId)
            .OrderBy(audit => audit.AttemptNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToAttempt).ToList();
    }

    private static RiotOrderCommandAttempt ToAttempt(RiotOrderCommandAuditRow row) => new(
        row.CommandAuditId,
        row.CommandType,
        row.AgvId,
        row.TargetUpperId,
        row.TargetOrderId,
        row.AttemptNumber,
        row.RequestSemanticSha256,
        row.IssuedAt,
        row.Outcome,
        row.ReconciledAt,
        row.ReceiptJson,
        row.FaultGeneration);
}

/// <summary>Storage for catalog availability and the pre-create gate's audit trail (ticket 13).</summary>
public sealed class CatalogAvailabilityStore(ControlServerDbContext dbContext) : ICatalogAvailabilityStore
{
    public async Task<MapStationCatalogAvailability?> ReadStateAsync(int mapId, CancellationToken cancellationToken)
    {
        MapStationCatalogStateRow? row = await dbContext.MapStationCatalogStates
            .AsNoTracking()
            .SingleOrDefaultAsync(state => state.MapId == mapId, cancellationToken)
            .ConfigureAwait(false);

        return row is null
            ? null
            : new MapStationCatalogAvailability(
                row.MapId,
                row.State,
                row.LastCompleteConfirmationAt,
                row.LastAttemptAt,
                row.LastFailureReason,
                row.CatalogRevision,
                row.ApprovedSyncPeriodSeconds,
                row.ApprovedMaxUnconfirmedSeconds,
                row.UpdatedAt);
    }

    public async Task RecordCompleteConfirmationAsync(
        int mapId,
        long catalogRevision,
        int approvedSyncPeriodSeconds,
        int approvedMaxUnconfirmedSeconds,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        MapStationCatalogStateRow row = await GetOrCreateAsync(mapId, at, cancellationToken).ConfigureAwait(false);

        row.State = MapStationCatalogState.Fresh;
        row.LastCompleteConfirmationAt = at;
        row.LastAttemptAt = at;
        row.LastFailureReason = null;
        row.CatalogRevision = catalogRevision;
        row.ApprovedSyncPeriodSeconds = approvedSyncPeriodSeconds;
        row.ApprovedMaxUnconfirmedSeconds = approvedMaxUnconfirmedSeconds;
        row.UpdatedAt = at;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordFailureAsync(
        int mapId,
        MapStationCatalogState state,
        string reason,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (state == MapStationCatalogState.Fresh)
        {
            throw new ArgumentException("A failure cannot be recorded as Fresh.", nameof(state));
        }

        MapStationCatalogStateRow row = await GetOrCreateAsync(mapId, at, cancellationToken).ConfigureAwait(false);

        row.State = state;
        // LastCompleteConfirmationAt is deliberately untouched: a failed attempt is not a
        // confirmation, and freshness (REQ-0302) is measured from confirmations only.
        row.LastAttemptAt = at;
        row.LastFailureReason = reason;
        row.UpdatedAt = at;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FreezeDemandStationsAsync(
        string demandId,
        string transportDemandKey,
        IReadOnlyList<FrozenStationFact> stations,
        long catalogRevision,
        DateTimeOffset frozenAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(transportDemandKey);
        ArgumentNullException.ThrowIfNull(stations);

        List<FrozenDemandStationRow> existing = await dbContext.FrozenDemandStations
            .Where(frozen => frozen.DemandId == demandId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (FrozenStationFact station in stations)
        {
            FrozenDemandStationRow? already = existing.SingleOrDefault(row => row.Role == station.Role);
            if (already is not null)
            {
                // REQ-0305: frozen endpoints are not rewritten by a later catalog. Re-freezing the
                // same endpoint is a no-op; re-freezing a different one is a bug in the caller and
                // must be loud rather than silently overwriting a task's destination.
                if (already.MapId != station.MapId || already.StationId != station.StationId)
                {
                    throw new InvalidOperationException(
                        $"Demand {demandId} already froze {already.Role} at map {already.MapId} " +
                        $"station {already.StationId}; refusing to rewrite it to map {station.MapId} " +
                        $"station {station.StationId}.");
                }

                continue;
            }

            dbContext.FrozenDemandStations.Add(new FrozenDemandStationRow
            {
                DemandId = demandId,
                Role = station.Role,
                TransportDemandKey = transportDemandKey,
                MapId = station.MapId,
                StationId = station.StationId,
                StationName = station.StationName,
                CatalogRevision = catalogRevision,
                FrozenAt = frozenAt,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FrozenStationFact>> ReadFrozenStationsAsync(
        string demandId,
        CancellationToken cancellationToken)
    {
        return await dbContext.FrozenDemandStations
            .AsNoTracking()
            .Where(frozen => frozen.DemandId == demandId)
            .OrderBy(frozen => frozen.Role)
            .Select(frozen => new FrozenStationFact(
                frozen.Role,
                frozen.MapId,
                frozen.StationId,
                frozen.StationName))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RecordGateVerdictAsync(CreateGateEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        dbContext.CreateGateAudit.Add(new CreateGateAuditRow
        {
            GateAuditId = evaluation.GateAuditId,
            DemandId = evaluation.DemandId,
            TransportDemandKey = evaluation.TransportDemandKey,
            AgvId = evaluation.AgvId,
            MapId = evaluation.MapId,
            TargetStationId = evaluation.TargetStationId,
            Verdict = evaluation.Verdict,
            RiotRouteCostMm = evaluation.RiotRouteCostMm,
            GraphTraversalCostMm = evaluation.GraphTraversalCostMm,
            GraphReachable = evaluation.GraphReachable,
            ConflictDetail = evaluation.ConflictDetail,
            EvaluatedAt = evaluation.EvaluatedAt,
        });

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<MapStationCatalogStateRow> GetOrCreateAsync(
        int mapId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        MapStationCatalogStateRow? row = await dbContext.MapStationCatalogStates
            .SingleOrDefaultAsync(state => state.MapId == mapId, cancellationToken)
            .ConfigureAwait(false);
        if (row is not null)
        {
            return row;
        }

        // A catalog nobody has confirmed yet is not Fresh. REQ-0302 makes the absence of an
        // approved, confirmed catalog a hard block, so the initial state must not read as usable.
        row = new MapStationCatalogStateRow
        {
            MapId = mapId,
            State = MapStationCatalogState.RefreshFailed,
            LastFailureReason = "CATALOG_NEVER_CONFIRMED",
            UpdatedAt = at,
        };
        dbContext.MapStationCatalogStates.Add(row);
        return row;
    }
}

/// <summary>
/// Fingerprint over a Map's edge-group membership, for the staleness rule ticket 12 implements.
/// </summary>
/// <remarks>
/// An empty group list yields an empty fingerprint rather than the hash of nothing, so that
/// "no groups" — map25's actual state — is distinguishable at a glance from a computed digest.
/// </remarks>
public static class RouteGraphEdgeGroupFingerprint
{
    public static string Compute(IReadOnlyList<RouteGraphEdgeGroupFact> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (groups.Count == 0)
        {
            return "";
        }

        // Length-prefixed canonical form. A group name may contain anything a person can type,
        // so any delimiter could appear inside a field and let two different memberships
        // canonicalize to the same string; a length prefix cannot.
        StringBuilder canonical = new();
        foreach (RouteGraphEdgeGroupFact group in groups
            .OrderBy(group => group.GroupName, StringComparer.Ordinal)
            .ThenBy(group => group.EdgeId)
            .ThenBy(group => group.GroupType, StringComparer.Ordinal))
        {
            AppendField(canonical, group.GroupName);
            AppendField(canonical, group.EdgeId.ToString(CultureInfo.InvariantCulture));
            AppendField(canonical, group.GroupType);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void AppendField(StringBuilder canonical, string value) => canonical
        .Append(value.Length.ToString(CultureInfo.InvariantCulture))
        .Append(':')
        .Append(value);
}
